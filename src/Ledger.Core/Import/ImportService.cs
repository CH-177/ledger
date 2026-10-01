using System.Text.RegularExpressions;
using Ledger.Core.Data;
using Ledger.Core.Forecasting;
using Ledger.Core.Payroll;
using Ledger.Core.Models;
using Ledger.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Core.Import;

public enum DuplicateKind
{
    None,

    /// <summary>The bank's own transaction ID is already in Ledger: this exact row was imported before.</summary>
    AlreadyImported,

    /// <summary>Same account, same amount and direction, within a few days: probably entered by hand or imported earlier.</summary>
    Likely,
}

/// <summary>One file row as it will be imported, with Ledger's suggestions. The user can change any of it in review.</summary>
public sealed record ImportCandidate(
    int Index,
    ImportedRow Row,
    TransactionType Type,
    string Payee,
    int? CategoryId,
    int? TransferAccountId,
    DuplicateKind Duplicate,
    string? DuplicateText,
    string? AttentionText,
    bool FromRememberedRule,
    BillMatch? Bill = null,
    PaycheckMatch? Paycheck = null)
{
    /// <summary>New rows are ticked; anything that may already be in Ledger starts unticked.</summary>
    public bool IncludeByDefault => Duplicate == DuplicateKind.None;
}

/// <summary>A bill (or expected income) occurrence a bank row looks like the payment of, not yet recorded.</summary>
public sealed record BillMatch(int RecurringTransactionId, DateOnly Date, string Name, TransactionType Type);

public sealed record ImportPreview(int AccountId, IReadOnlyList<ImportCandidate> Rows, IReadOnlyList<string> Problems)
{
    public int NewCount => Rows.Count(r => r.Duplicate == DuplicateKind.None);
    public int DuplicateCount => Rows.Count - NewCount;
}

/// <summary>What the user decided for one row in review.</summary>
public sealed record ImportDecision(int Index, TransactionType Type, string Payee, int? CategoryId, int? TransferAccountId = null, bool RememberPayee = false,
    BillMatch? Bill = null, PaycheckMatch? Paycheck = null);

/// <summary>A predicted paycheck, not yet recorded, that a deposit looks like the payment of.</summary>
public sealed record PaycheckMatch(int JobId, string JobName, PayPeriod Period, long EstimatedNetCents);

public sealed record ImportResult(int BatchId, int Imported, int SkippedAsAlreadyImported);

/// <summary>
/// Bank-file import (brief §25–26): parsed rows → suggestions and duplicate checks → the user reviews
/// → saved together as one batch that can be undone. Nothing is saved without review; nothing is
/// ever silently duplicated; the file itself is only read, never stored.
/// </summary>
public sealed class ImportService(IDbContextFactory<LedgerDbContext> contextFactory, TimeProvider time, IncomeForecastService? paychecks = null)
{
    /// <summary>How many days apart a bank row and an existing transaction can be and still be the same thing.</summary>
    public const int DuplicateWindowDays = 3;

    // ---- Profiles (how to read each account's file) --------------------------------------------

    public async Task<CsvMapping?> GetCsvMappingAsync(int accountId, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var p = await db.ImportProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.AccountId == accountId, ct);
        return p is { DateColumn: { } date, DescriptionColumn: { } desc }
            ? new CsvMapping(date, desc, p.AmountColumn, p.DebitColumn, p.CreditColumn, p.MemoColumn, p.ReferenceColumn, p.DateFormat, p.InvertSign, p.BalanceColumn)
            : null;
    }

    /// <summary>The account a bank file's own account id was matched to before, if any.</summary>
    public async Task<int?> FindAccountForBankKeyAsync(string? bankKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(bankKey)) return null;
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.ImportProfiles.AsNoTracking().Where(p => p.BankAccountKey == bankKey.Trim()).Select(p => (int?)p.AccountId).FirstOrDefaultAsync(ct);
    }

    // ---- Preview ---------------------------------------------------------------------------------

    public async Task<ImportPreview> PreviewAsync(int accountId, ParsedFile file, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId, ct)
            ?? throw new LedgerValidationException("Choose the account this file belongs to.");
        if (account.IsArchived)
        {
            throw new LedgerValidationException($"“{account.Name}” is archived. Restore it before importing into it.");
        }
        if (file.Rows.Count == 0)
        {
            return new ImportPreview(accountId, [], file.Problems.Count > 0 ? file.Problems : ["This file has no transactions."]);
        }

        var from = file.Rows.Min(r => r.Date).AddDays(-DuplicateWindowDays);
        var to = file.Rows.Max(r => r.Date).AddDays(DuplicateWindowDays);
        var existing = await db.Transactions.AsNoTracking()
            .Where(t => (t.AccountId == accountId || t.TransferAccountId == accountId) && t.Date >= from && t.Date <= to)
            .Select(t => new Existing(t.Id, t.Date, t.AccountId, t.TransferAccountId, t.TransactionType, t.AmountCents, t.Payee, t.ExternalId))
            .ToListAsync(ct);
        var ids = file.Rows.Where(r => r.ExternalId is not null).Select(r => r.ExternalId!).Distinct().ToList();
        var importedIds = (await db.Transactions.AsNoTracking()
                .Where(t => t.ExternalId != null && ids.Contains(t.ExternalId) && (t.AccountId == accountId || t.TransferAccountId == accountId))
                .Select(t => t.ExternalId!).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        var rules = await db.ImportPayeeRules.AsNoTracking().ToDictionaryAsync(r => r.Key, ct);
        var otherAccounts = await db.Accounts.AsNoTracking().Where(a => a.Id != accountId && !a.IsArchived)
            .Select(a => new { a.Id, a.Name }).ToListAsync(ct);
        var shareKeys = await db.ImportProfiles.AsNoTracking().Where(p => p.AccountId != accountId && p.BankAccountKey != null)
            .Select(p => new { p.AccountId, p.BankAccountKey }).ToListAsync(ct);
        var history = await db.Transactions.AsNoTracking()
            .Where(t => t.Payee != null && t.CategoryId != null)
            .GroupBy(t => new { t.Payee, t.TransactionType, t.CategoryId })
            .Select(g => new { g.Key.Payee, g.Key.TransactionType, g.Key.CategoryId, Count = g.Count() })
            .ToListAsync(ct);
        var categoryTypes = await db.Categories.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.CategoryType, ct);

        var claimed = new HashSet<int>();                 // each existing transaction matches at most one row
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<ImportCandidate>();
        for (var i = 0; i < file.Rows.Count; i++)
        {
            var row = file.Rows[i];
            var moneyIn = row.AmountCents > 0;
            var magnitude = Math.Abs(row.AmountCents);

            // Suggested type. Money into a credit card is usually a payment from another account.
            var type = moneyIn ? TransactionType.Income : TransactionType.Expense;
            string? attention = null;
            if (moneyIn && account.IsLiability)
            {
                type = TransactionType.Transfer;
                attention = "Money into a card: a payment (choose the account it came from) or a refund.";
            }

            // Payee and category: a remembered rule first, then what this payee was filed under most often.
            var key = PayeeCleaner.RuleKey(row.Description);
            var fromRule = rules.TryGetValue(key, out var rule);
            var payee = fromRule ? rule!.Payee : PayeeCleaner.Clean(row.Description);
            var category = fromRule
                ? rule!.CategoryId
                : history.Where(h => h.TransactionType == type && string.Equals(h.Payee, payee, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(h => h.Count).Select(h => h.CategoryId).FirstOrDefault();
            // Money moved between the user's own accounts: remembered, or named in "Transfer from/to …".
            // A transfer with someone else's account (a family member's) is neither income nor spending.
            int? transferAccount = null;
            if (fromRule && rule!.IsOutsideTransfer)
            {
                type = moneyIn ? TransactionType.OutsideIn : TransactionType.OutsideOut;
                attention = null;
            }
            else if (fromRule && rule!.TransferAccountId is { } remembered && otherAccounts.Any(a => a.Id == remembered))
            {
                type = TransactionType.Transfer;
                transferAccount = remembered;
                attention = null;
            }
            else if (!fromRule && IsTransferDescription(row.Description))
            {
                // Example CU names shares, not accounts: "Share 0001" is one of yours unless another
                // member's (masked) number comes with it. Known once that share's file has been imported.
                var someoneElses = OtherMemberNumber().IsMatch(row.Description);
                var share = someoneElses ? null : ShareNumber().Match(row.Description) is { Success: true } m ? m.Groups[1].Value : null;
                var named = someoneElses ? [] : otherAccounts.Where(a => NamesAccount(row.Description, a.Name)
                    || share is not null && shareKeys.Any(p => p.AccountId == a.Id && p.BankAccountKey!.EndsWith("S" + share, StringComparison.OrdinalIgnoreCase))).ToList();
                if (named.Count == 0 && share is not null && !account.IsLiability)
                {
                    type = TransactionType.Transfer;
                    attention = $"Looks like a transfer {(moneyIn ? "from" : "to")} another of your shares (Share {share}). Choose which account that is, and tick Remember so next time it's automatic.";
                }
                else if (named.Count == 1)
                {
                    type = TransactionType.Transfer;
                    transferAccount = named[0].Id;
                    attention = $"Looks like a transfer {(moneyIn ? "from" : "to")} your {named[0].Name} account. If it's someone else's money, choose {(moneyIn ? "Income" : "Spending")}.";
                }
                else if (named.Count == 0 && !account.IsLiability)
                {
                    type = moneyIn ? TransactionType.OutsideIn : TransactionType.OutsideOut;
                    attention = $"Looks like a transfer {(moneyIn ? "from" : "to")} someone else's account: it won't count as {(moneyIn ? "income" : "spending")}. If it's one of your accounts, choose it; if it's {(moneyIn ? "income (a gift or pay)" : "a payment for something")}, choose {(moneyIn ? "Income" : "Spending")}. Tick Remember so next time it's automatic.";
                }
                else
                {
                    attention = $"If this moved money between your own accounts, choose Transfer and the other account. Tick Remember so next time it's automatic.";
                }
            }

            // Money back from somewhere you spend: a refund, which lowers spending instead of counting as income.
            if (moneyIn && type == TransactionType.Income || moneyIn && account.IsLiability && type == TransactionType.Transfer && transferAccount is null)
            {
                var spentHere = history.Where(h => h.TransactionType == TransactionType.Expense && string.Equals(h.Payee, payee, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(h => h.Count).Select(h => h.CategoryId).FirstOrDefault();
                var ruleSaysSpending = fromRule && category is { } c && categoryTypes.GetValueOrDefault(c) == CategoryType.Expense;
                if (ruleSaysSpending || !fromRule && (IsRefundDescription(row.Description) || spentHere is not null))
                {
                    type = TransactionType.Refund;
                    category = ruleSaysSpending ? category : spentHere;
                    attention = fromRule ? null : $"Looks like a refund from {payee}: it lowers spending instead of counting as income. If it's income, choose Income.";
                }
            }
            if (type is TransactionType.Transfer or TransactionType.OutsideIn or TransactionType.OutsideOut) category = null;
            // A remembered or usual category must suit the suggested type (a Venmo rule filed as income
            // shouldn't put a Venmo payment out into an income category).
            if (category is { } cat && categoryTypes.GetValueOrDefault(cat) != (type == TransactionType.Income ? CategoryType.Income : CategoryType.Expense))
            {
                category = null;
            }

            // Duplicates: exact bank ID here; nearby transactions with the same effect below.
            var alreadyImported = row.ExternalId is { } ext && (importedIds.Contains(ext) || !seenIds.Add(ext));
            rows.Add(new ImportCandidate(i, row, type, payee, category, transferAccount,
                alreadyImported ? DuplicateKind.AlreadyImported : DuplicateKind.None,
                alreadyImported ? "Already imported (same bank transaction ID)." : null, attention, fromRule));
        }

        // Likely duplicates, in two passes: rows whose payee also looks the same are paired first, so
        // of two $20 withdrawals on one day, the transfer (not the other one) claims the transfer already in Ledger.
        foreach (var payeeMustMatch in new[] { true, false })
        {
            for (var j = 0; j < rows.Count; j++)
            {
                var c = rows[j];
                if (c.Duplicate == DuplicateKind.None && FindLikely(existing, claimed, accountId, c.Row, c.Payee, payeeMustMatch) is { } match)
                {
                    claimed.Add(match.Id);
                    rows[j] = c with
                    {
                        Duplicate = DuplicateKind.Likely,
                        DuplicateText = $"Probably already in Ledger: {match.Payee ?? match.Type.ToString()} on {match.Date:MMM d}, {Money.Format(match.AmountCents)}.",
                    };
                }
            }
        }

        // Bills: a new spending (or income) row that looks like the payment of a bill occurrence not
        // yet recorded counts as that payment, so the bill stops showing as "Needs recording".
        var bills = await OpenBillsAsync(db, accountId, from.AddDays(-BillWindowDays), to.AddDays(BillWindowDays), ct);
        var usedBills = new HashSet<(int, DateOnly)>();
        for (var j = 0; j < rows.Count; j++)
        {
            var c = rows[j];
            if (c.Duplicate != DuplicateKind.None || c.Type is not (TransactionType.Expense or TransactionType.Income))
            {
                continue;
            }
            var magnitude = Math.Abs(c.Row.AmountCents);
            var match = bills
                .Where(b => b.Rule.TransactionType == c.Type && !usedBills.Contains((b.Rule.Id, b.Date)))
                .Select(b => (b, days: Math.Abs(b.Date.DayNumber - c.Row.Date.DayNumber),
                    exact: b.Rule.AmountCents == magnitude,
                    similar: SimilarPayee(b.Rule.Payee ?? b.Rule.Name, c.Payee) || SimilarPayee(b.Rule.Name, c.Payee)))
                .Where(x => x.days <= BillWindowDays
                    && (x.exact || x.b.Rule.IsAmountEstimated && Math.Abs(x.b.Rule.AmountCents - magnitude) * 5 <= x.b.Rule.AmountCents)
                    && (x.similar || x.exact))
                .OrderByDescending(x => x.similar).ThenBy(x => x.days)
                .Select(x => x.b)
                .FirstOrDefault();
            if (match is not null)
            {
                usedBills.Add((match.Rule.Id, match.Date));
                rows[j] = c with
                {
                    Bill = new BillMatch(match.Rule.Id, match.Date, match.Rule.Name, match.Rule.TransactionType),
                    CategoryId = c.CategoryId ?? match.Rule.CategoryId,
                };
            }
        }

        // Paychecks: a deposit into a job's pay account near a predicted payday, from the employer (or
        // close to the estimate), is that paycheck. Linking it stops the estimate counting as well.
        var jobs = await db.Jobs.AsNoTracking().Where(j => j.DepositAccountId == accountId).Select(j => new { j.Id, j.IncomeCategoryId }).ToListAsync(ct);
        if (paychecks is not null && jobs.Count > 0)
        {
            var jobIds = jobs.Select(j => j.Id).ToHashSet();
            var open = (await paychecks.GetPaychecksAsync(from.AddDays(-PaycheckWindowDays), to.AddDays(PaycheckWindowDays), ct))
                .Where(p => p.Recorded is null && p.NetCents > 0 && jobIds.Contains(p.JobId)).ToList();
            for (var j = 0; j < rows.Count && open.Count > 0; j++)
            {
                var c = rows[j];
                if (c.Duplicate != DuplicateKind.None || c.Type != TransactionType.Income || c.Bill is not null)
                {
                    continue;
                }
                var amount = c.Row.AmountCents;
                var match = open
                    .Select(p => (p, days: Math.Abs(p.Period.PayDate.DayNumber - c.Row.Date.DayNumber), similar: SimilarPayee(p.JobName, c.Payee)))
                    .Where(x => x.days <= PaycheckWindowDays && (x.similar || Math.Abs(x.p.NetCents - amount) * 4 <= x.p.NetCents))
                    .OrderByDescending(x => x.similar).ThenBy(x => x.days)
                    .Select(x => x.p)
                    .FirstOrDefault();
                if (match is not null)
                {
                    open.Remove(match);
                    rows[j] = c with
                    {
                        Paycheck = new PaycheckMatch(match.JobId, match.JobName, match.Period, match.NetCents),
                        CategoryId = c.CategoryId ?? jobs.First(x => x.Id == match.JobId).IncomeCategoryId,
                    };
                }
            }
        }
        return new ImportPreview(accountId, rows, file.Problems);
    }

    /// <summary>How many days a deposit can be from a predicted payday and still be that paycheck.</summary>
    public const int PaycheckWindowDays = 5;

    /// <summary>How many days a bank row can be from a bill's due date and still be its payment.</summary>
    public const int BillWindowDays = 5;

    private sealed record OpenBill(RecurringTransaction Rule, DateOnly Date);

    /// <summary>Occurrences of this account's active bills and expected income that are neither recorded nor skipped.</summary>
    private static async Task<List<OpenBill>> OpenBillsAsync(LedgerDbContext db, int accountId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var rules = await db.RecurringTransactions.AsNoTracking()
            .Where(r => r.IsActive && r.AccountId == accountId && r.TransactionType != TransactionType.Transfer)
            .ToListAsync(ct);
        if (rules.Count == 0) return [];
        var ruleIds = rules.Select(r => r.Id).ToList();
        var recorded = (await db.Transactions.AsNoTracking()
                .Where(t => t.RecurringTransactionId != null && ruleIds.Contains(t.RecurringTransactionId.Value) && t.RecurrenceDate >= from && t.RecurrenceDate <= to)
                .Select(t => new { Rule = t.RecurringTransactionId!.Value, Date = t.RecurrenceDate!.Value }).ToListAsync(ct))
            .Select(x => (x.Rule, x.Date)).ToHashSet();
        var skipped = (await db.RecurringSkips.AsNoTracking()
                .Where(s => ruleIds.Contains(s.RecurringTransactionId) && s.Date >= from && s.Date <= to)
                .Select(s => new { Rule = s.RecurringTransactionId, s.Date }).ToListAsync(ct))
            .Select(x => (x.Rule, x.Date)).ToHashSet();
        return rules
            .SelectMany(r => RecurrenceSchedule.Between(r.StartDate, r.EndDate, r.Frequency, from, to).Select(d => new OpenBill(r, d)))
            .Where(b => !recorded.Contains((b.Rule.Id, b.Date)) && !skipped.Contains((b.Rule.Id, b.Date)))
            .ToList();
    }

    /// <summary>Bank wording for money coming back for a purchase ("REFUND", "RETURN", "MERCHANDISE CREDIT").</summary>
    internal static bool IsRefundDescription(string description) =>
        Regex.IsMatch(description, @"\b(REFUND|RETURN|RETURNED|MERCHANDISE CREDIT)\b", RegexOptions.IgnoreCase);

    /// <summary>Bank wording for money moved between accounts ("Transfer from Main Savings (S0001)", "Online transfer to …").</summary>
    internal static bool IsTransferDescription(string description) =>
        Regex.IsMatch(description, @"\b(TRANSFER\s+((SENT|RECEIVED)\s+)?|MOBILE\s+|ONLINE BANKING\s+)(FROM|TO)\b", RegexOptions.IgnoreCase);

    /// <summary>Example CU's share number in a transfer: "SHARE 0001".</summary>
    private static Regex ShareNumber() => new(@"\bSHARE\s+(\d{4})\b", RegexOptions.IgnoreCase);

    /// <summary>Another member's masked number ("1234XXXXXX"): the other side is someone else's account.</summary>
    private static Regex OtherMemberNumber() => new(@"\b\d+X{3,}\b", RegexOptions.IgnoreCase);

    /// <summary>Whether the description names a Ledger account as whole words ("Main Savings" names "Savings" but "Savingsbank" doesn't).</summary>
    internal static bool NamesAccount(string description, string accountName) =>
        accountName.Trim().Length >= 3 &&
        Regex.IsMatch(description, $@"\b{Regex.Escape(accountName.Trim())}\b", RegexOptions.IgnoreCase);

    private sealed record Existing(int Id, DateOnly Date, int AccountId, int? TransferAccountId, TransactionType Type, long AmountCents, string? Payee, string? ExternalId);

    /// <summary>
    /// A transaction already in Ledger with the same effect on this account (same amount and
    /// direction) within the window. Same day always counts; a few days apart counts when the payee
    /// looks the same or the existing one has no payee. With <paramref name="payeeMustMatch"/>, only
    /// a similar payee counts.
    /// </summary>
    private static Existing? FindLikely(List<Existing> existing, HashSet<int> claimed, int accountId, ImportedRow row, string payee, bool payeeMustMatch)
    {
        var moneyIn = row.AmountCents > 0;
        return existing
            .Where(e => !claimed.Contains(e.Id) && e.AmountCents == Math.Abs(row.AmountCents) && EffectIsIn(e, accountId) == moneyIn)
            .Select(e => (e, days: Math.Abs(e.Date.DayNumber - row.Date.DayNumber), similar: e.Payee is not null && SimilarPayee(e.Payee, payee)))
            .Where(x => x.days <= DuplicateWindowDays && (payeeMustMatch ? x.similar : x.days == 0 || x.e.Payee is null || x.similar))
            .OrderBy(x => x.days)
            .Select(x => x.e)
            .FirstOrDefault();
    }

    /// <summary>Whether an existing transaction put money into <paramref name="accountId"/> (true) or took it out.</summary>
    private static bool EffectIsIn(Existing e, int accountId) => e.Type switch
    {
        TransactionType.Income or TransactionType.Refund or TransactionType.OutsideIn => true,
        TransactionType.OutsideOut => false,
        TransactionType.Expense => false,
        _ => e.TransferAccountId == accountId,
    };

    internal static bool SimilarPayee(string a, string b)
    {
        static string First(string s) => new string(s.Trim().ToUpperInvariant().TakeWhile(char.IsLetterOrDigit).ToArray());
        var x = First(a);
        var y = First(b);
        return x.Length >= 3 && y.Length >= 3 && (x.StartsWith(y, StringComparison.Ordinal) || y.StartsWith(x, StringComparison.Ordinal));
    }

    // ---- Commit ----------------------------------------------------------------------------------

    /// <summary>
    /// Saves the ticked rows as one batch, in one database transaction: all or nothing. Rows whose
    /// bank ID is already in Ledger are skipped even if ticked, so importing the same file twice can
    /// never duplicate. Remembers the CSV layout (or QFX account id) and any payee corrections the
    /// user asked to remember.
    /// </summary>
    public async Task<ImportResult> CommitAsync(int accountId, ParsedFile file, IReadOnlyList<ImportDecision> decisions, string fileName,
        CsvMapping? mapping = null, CancellationToken ct = default)
    {
        if (decisions.Count == 0)
        {
            throw new LedgerValidationException("Tick at least one row to import.");
        }
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var batch = new ImportBatch
        {
            AccountId = accountId,
            FileName = Path.GetFileName(fileName) is { Length: > 0 } n ? n[..Math.Min(n.Length, 260)] : "import",
            ImportedAt = time.GetUtcNow().UtcDateTime,
        };
        db.ImportBatches.Add(batch);
        await db.SaveChangesAsync(ct);

        var ids = file.Rows.Where(r => r.ExternalId is not null).Select(r => r.ExternalId!).ToList();
        var already = (await db.Transactions.AsNoTracking()
                .Where(t => t.ExternalId != null && ids.Contains(t.ExternalId) && (t.AccountId == accountId || t.TransferAccountId == accountId))
                .Select(t => t.ExternalId!).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        var imported = 0;
        var skipped = 0;
        var paychecksPaid = new List<(Transaction Transaction, PaycheckMatch Paycheck)>();
        foreach (var d in decisions.OrderBy(d => d.Index))
        {
            if (d.Index < 0 || d.Index >= file.Rows.Count)
            {
                throw new LedgerValidationException("The file changed during review. Open it again.");
            }
            var row = file.Rows[d.Index];
            if (row.ExternalId is { } ext && !already.Add(ext))
            {
                skipped++;
                continue;
            }

            var amount = Math.Abs(row.AmountCents);
            var moneyIn = row.AmountCents > 0;
            // A transfer's direction comes from the file: money into this account came from the other one.
            var (from, to) = d.Type == TransactionType.Transfer
                ? moneyIn ? (d.TransferAccountId ?? 0, (int?)accountId) : (accountId, d.TransferAccountId)
                : (accountId, null);
            var input = new TransactionInput(d.Type, amount, row.Date, from, to, d.CategoryId, d.Payee, row.Memo);
            try
            {
                await TransactionService.ValidateAsync(db, input, existing: null, ct);
            }
            catch (LedgerValidationException ex)
            {
                throw new LedgerValidationException($"Row {d.Index + 1} ({row.Date:MMM d}, {Money.Format(row.AmountCents)}): {ex.Message}");
            }
            var fitsDirection = d.Type switch
            {
                TransactionType.Income or TransactionType.Refund or TransactionType.OutsideIn => moneyIn,
                TransactionType.Expense or TransactionType.OutsideOut => !moneyIn,
                _ => true,
            };
            if (!fitsDirection)
            {
                var kind = d.Type switch
                {
                    TransactionType.Income => "income",
                    TransactionType.Refund => "a refund",
                    TransactionType.OutsideIn => "a transfer in",
                    TransactionType.OutsideOut => "a transfer out",
                    _ => "spending",
                };
                throw new LedgerValidationException(
                    $"Row {d.Index + 1} ({row.Date:MMM d}, {Money.Format(row.AmountCents)}): money {(moneyIn ? "into" : "out of")} the account can't be {kind}.");
            }

            // The payment of a bill: linked to its occurrence, unless that was recorded meanwhile. A
            // deleted transaction still holding the slot lets go of it first (unique index), as in RecurringService.
            BillMatch? bill = null;
            if (d.Bill is { } b && b.Type == d.Type
                && !db.Transactions.Local.Any(t => t.RecurringTransactionId == b.RecurringTransactionId && t.RecurrenceDate == b.Date))
            {
                var holders = await db.Transactions.IgnoreQueryFilters()
                    .Where(t => t.RecurringTransactionId == b.RecurringTransactionId && t.RecurrenceDate == b.Date).ToListAsync(ct);
                if (holders.All(t => t.IsDeleted))
                {
                    foreach (var deleted in holders)
                    {
                        deleted.RecurringTransactionId = null;
                        deleted.RecurrenceDate = null;
                    }
                    await db.SaveChangesAsync(ct);
                    db.RecurringSkips.RemoveRange(db.RecurringSkips.Where(s => s.RecurringTransactionId == b.RecurringTransactionId && s.Date == b.Date));
                    bill = b;
                }
            }

            var added = new Transaction
            {
                RecurringTransactionId = bill?.RecurringTransactionId,
                RecurrenceDate = bill?.Date,
                TransactionType = d.Type,
                AmountCents = amount,
                Date = row.Date,
                AccountId = from,
                TransferAccountId = to,
                CategoryId = d.CategoryId,
                Payee = string.IsNullOrWhiteSpace(d.Payee) ? null : d.Payee.Trim(),
                Memo = row.Memo,
                Status = TransactionStatus.Cleared,
                ExternalId = row.ExternalId,
                ImportBatchId = batch.Id,
            };
            db.Transactions.Add(added);
            if (d.Paycheck is { } pay && d.Type == TransactionType.Income)
            {
                paychecksPaid.Add((added, pay));
            }
            imported++;

            if (d.RememberPayee && !string.IsNullOrWhiteSpace(d.Payee))
            {
                var key = PayeeCleaner.RuleKey(row.Description);
                var rule = await db.ImportPayeeRules.FirstOrDefaultAsync(r => r.Key == key, ct)
                           ?? db.ImportPayeeRules.Local.FirstOrDefault(r => r.Key == key);
                if (rule is null)
                {
                    db.ImportPayeeRules.Add(rule = new ImportPayeeRule { Key = key, Payee = "" });
                }
                rule.Payee = d.Payee.Trim();
                var outside = d.Type is TransactionType.OutsideIn or TransactionType.OutsideOut;
                rule.CategoryId = d.Type == TransactionType.Transfer || outside ? null : d.CategoryId;
                rule.TransferAccountId = d.Type == TransactionType.Transfer ? d.TransferAccountId : null;
                rule.IsOutsideTransfer = outside;
            }
        }

        batch.TransactionCount = imported;
        await SaveProfileAsync(db, accountId, file.BankAccountKey, mapping, ct);
        await db.SaveChangesAsync(ct);

        // Deposits that pay a predicted paycheck are linked to it, exactly as recording it on the Income
        // page does, so the estimate stops counting in the forecast. A record whose transaction was
        // deleted is reused (unique per job and pay period).
        foreach (var (transaction, pay) in paychecksPaid)
        {
            var record = await db.PaycheckRecords.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.JobId == pay.JobId && r.PeriodStart == pay.Period.Start, ct);
            if (record is not null && await db.Transactions.AnyAsync(t => t.Id == record.TransactionId && !t.IsDeleted, ct))
            {
                continue; // recorded meanwhile: keep that one
            }
            if (record is null)
            {
                db.PaycheckRecords.Add(record = new PaycheckRecord { JobId = pay.JobId, PeriodStart = pay.Period.Start });
            }
            record.PayDate = pay.Period.PayDate;
            record.EstimatedNetCents = pay.EstimatedNetCents;
            record.TransactionId = transaction.Id;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new ImportResult(batch.Id, imported, skipped);
    }

    private static async Task SaveProfileAsync(LedgerDbContext db, int accountId, string? bankKey, CsvMapping? mapping, CancellationToken ct)
    {
        var p = await db.ImportProfiles.FirstOrDefaultAsync(x => x.AccountId == accountId, ct);
        if (p is null)
        {
            db.ImportProfiles.Add(p = new ImportProfile { AccountId = accountId });
        }
        if (!string.IsNullOrWhiteSpace(bankKey))
        {
            p.BankAccountKey = bankKey.Trim();
        }
        if (mapping is not null)
        {
            (p.DateColumn, p.DescriptionColumn, p.AmountColumn, p.DebitColumn, p.CreditColumn, p.MemoColumn, p.ReferenceColumn, p.DateFormat, p.InvertSign, p.BalanceColumn) =
                (mapping.Date, mapping.Description, mapping.Amount, mapping.Debit, mapping.Credit, mapping.Memo, mapping.Reference, mapping.DateFormat, mapping.InvertSign, mapping.Balance);
        }
    }

    // ---- History and undo -------------------------------------------------------------------------

    public async Task<IReadOnlyList<ImportBatch>> GetRecentBatchesAsync(int take = 10, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.ImportBatches.AsNoTracking().Include(b => b.Account)
            .OrderByDescending(b => b.ImportedAt).Take(take).ToListAsync(ct);
    }

    /// <summary>
    /// Removes every transaction this import created (a normal delete: hidden everywhere, kept on disk).
    /// Other transactions, including ones typed by hand, are untouched. The file can be imported again.
    /// </summary>
    public async Task<int> UndoAsync(int batchId, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var batch = await db.ImportBatches.FirstOrDefaultAsync(b => b.Id == batchId, ct)
            ?? throw new LedgerValidationException("That import no longer exists.");
        if (batch.IsUndone)
        {
            return 0;
        }
        var now = time.GetUtcNow().UtcDateTime;
        var rows = await db.Transactions.Where(t => t.ImportBatchId == batchId).ToListAsync(ct);
        foreach (var t in rows)
        {
            t.IsDeleted = true;
            t.DeletedAt = now;
            // A deleted import row must not stop the same bank row being imported again.
            t.ExternalId = null;
        }
        batch.IsUndone = true;
        await db.SaveChangesAsync(ct);
        return rows.Count;
    }
}

using Ledger.Core.Data;
using Ledger.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Core.Services;

/// <summary>A transaction that can be ticked off against a statement, with its effect on the account (+ in, − out).</summary>
public sealed record ReconcileItem(int TransactionId, DateOnly Date, string Title, long EffectCents, TransactionStatus Status, bool AfterStatement)
{
    public bool IsPending => Status == TransactionStatus.Pending;
}

/// <summary>
/// Where a reconciliation starts: the balance already reconciled up to the statement date, and the
/// account's other transactions up to it (plus a week after, which a bank sometimes posts earlier).
/// </summary>
public sealed record ReconcileStart(
    int AccountId,
    string AccountName,
    bool IsLiability,
    DateOnly StatementDate,
    long ReconciledBalanceCents,
    IReadOnlyList<ReconcileItem> Items,
    Reconciliation? Previous)
{
    /// <summary>The balance the statement should show if exactly the ticked transactions are on it.</summary>
    public long ClearedBalanceCents(IReadOnlySet<int> ticked) =>
        ReconciledBalanceCents + Items.Where(i => ticked.Contains(i.TransactionId)).Sum(i => i.EffectCents);

    /// <summary>Statement minus cleared balance: zero means they match.</summary>
    public long DifferenceCents(long statementBalanceCents, IReadOnlySet<int> ticked) => statementBalanceCents - ClearedBalanceCents(ticked);

    /// <summary>Ticked by default: cleared transactions on or before the statement date. Pending ones and later ones are left for you.</summary>
    public IReadOnlySet<int> DefaultTicked => Items.Where(i => !i.AfterStatement && !i.IsPending).Select(i => i.TransactionId).ToHashSet();
}

/// <summary>
/// Statement reconciliation: match an account against a bank or card statement, tick what's on it,
/// and at a zero difference mark those transactions Reconciled. See docs/RULES.md ("Reconciliation").
/// </summary>
public sealed class ReconciliationService(IDbContextFactory<LedgerDbContext> contextFactory, TimeProvider time)
{
    /// <summary>Transactions dated up to this many days after the statement are offered too, marked as later.</summary>
    public const int AfterDays = 7;

    public async Task<ReconcileStart> StartAsync(int accountId, DateOnly statementDate, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId, ct)
            ?? throw new LedgerValidationException("That account no longer exists.");
        var anchor = account.BalanceDate;
        var until = statementDate.AddDays(AfterDays);
        // Transactions on or before the balance date are already inside the starting balance.
        var rows = await db.Transactions.AsNoTracking()
            .Where(t => (t.AccountId == accountId || t.TransferAccountId == accountId) && t.Date <= until && (anchor == null || t.Date > anchor))
            .OrderBy(t => t.Date).ThenBy(t => t.Id)
            .Select(t => new { t.Id, t.Date, t.Payee, t.TransactionType, t.AmountCents, t.AccountId, t.TransferAccountId, t.Status })
            .ToListAsync(ct);

        long reconciled = account.StartingBalanceCents;
        var items = new List<ReconcileItem>();
        foreach (var t in rows)
        {
            var effect = EffectOn(accountId, t.TransactionType, t.AmountCents, t.AccountId, t.TransferAccountId);
            if (t.Status == TransactionStatus.Reconciled)
            {
                if (t.Date <= statementDate) reconciled = checked(reconciled + effect);
                continue;
            }
            items.Add(new ReconcileItem(t.Id, t.Date, t.Payee ?? KindName(t.TransactionType), effect, t.Status, t.Date > statementDate));
        }
        var previous = await db.Reconciliations.AsNoTracking()
            .Where(r => r.AccountId == accountId && !r.IsUndone)
            .OrderByDescending(r => r.StatementDate).ThenByDescending(r => r.Id)
            .FirstOrDefaultAsync(ct);
        return new ReconcileStart(accountId, account.Name, account.IsLiability, statementDate, reconciled, items, previous);
    }

    /// <summary>
    /// Likely causes of a difference, most specific first (at most four): a transaction whose ticking
    /// or unticking would fix it exactly, one dated just after the statement, a pending one, or an amount
    /// with two digits swapped (a difference divisible by 9).
    /// </summary>
    public static IReadOnlyList<string> Hints(ReconcileStart start, long statementBalanceCents, IReadOnlySet<int> ticked)
    {
        var diff = start.DifferenceCents(statementBalanceCents, ticked);
        if (diff == 0) return [];
        var hints = new List<string>();
        string Describe(ReconcileItem i) => $"“{i.Title}” ({i.Date:MMM d}, {Money.Format(i.EffectCents, MoneySign.Always)})";

        foreach (var i in start.Items.Where(i => !ticked.Contains(i.TransactionId) && i.EffectCents == diff))
        {
            hints.Add(i.AfterStatement
                ? $"{Describe(i)} is dated after the statement, but ticking it would make them match. Banks sometimes post a day or two early."
                : i.IsPending
                    ? $"{Describe(i)} is marked pending, but ticking it would make them match."
                    : $"Ticking {Describe(i)} would make them match.");
        }
        foreach (var i in start.Items.Where(i => ticked.Contains(i.TransactionId) && i.EffectCents == -diff))
        {
            hints.Add($"{Describe(i)} may not be on the statement: unticking it would make them match.");
        }
        if (Math.Abs(diff) % 9 == 0)
        {
            foreach (var i in start.Items.Where(i => ticked.Contains(i.TransactionId)))
            {
                var magnitude = Math.Abs(i.EffectCents);
                var sign = Math.Sign(i.EffectCents);
                foreach (var swapped in AdjacentSwaps(magnitude).Where(s => sign * (s - magnitude) == diff))
                {
                    hints.Add($"{Describe(i)}: if the statement shows {Money.Format(swapped)}, two digits were swapped.");
                }
            }
        }
        if (hints.Count == 0)
        {
            hints.Add(diff > 0
                ? $"The statement shows {Money.Format(diff)} more than the ticked transactions: something that added money is missing, or an amount is off."
                : $"The statement shows {Money.Format(-diff)} less than the ticked transactions: something that took money out is missing, or an amount is off.");
        }
        return hints.Distinct().Take(4).ToList();
    }

    /// <summary>
    /// Marks the ticked transactions Reconciled and saves the statement. Refused unless the statement
    /// balance and the cleared balance match exactly.
    /// </summary>
    public async Task<Reconciliation> FinishAsync(int accountId, DateOnly statementDate, long statementBalanceCents, IReadOnlyCollection<int> tickedIds, CancellationToken ct = default)
    {
        var start = await StartAsync(accountId, statementDate, ct);
        var ticked = tickedIds.ToHashSet();
        if (ticked.Any(id => start.Items.All(i => i.TransactionId != id)))
        {
            throw new LedgerValidationException("A ticked transaction changed or is already reconciled. Start the reconciliation again.");
        }
        var diff = start.DifferenceCents(statementBalanceCents, ticked);
        if (diff != 0)
        {
            throw new LedgerValidationException($"It's still {Money.Format(Math.Abs(diff))} off. Reconcile when the difference is $0.00.");
        }

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var reconciliation = new Reconciliation
        {
            AccountId = accountId,
            StatementDate = statementDate,
            StatementBalanceCents = statementBalanceCents,
            ReconciledAt = time.GetUtcNow().UtcDateTime,
            TransactionCount = ticked.Count,
        };
        db.Reconciliations.Add(reconciliation);
        await db.SaveChangesAsync(ct);
        foreach (var t in await db.Transactions.Where(t => ticked.Contains(t.Id)).ToListAsync(ct))
        {
            t.Status = TransactionStatus.Reconciled;
            t.ReconciliationId = reconciliation.Id;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return reconciliation;
    }

    /// <summary>Past statements for the account, newest first (undone ones left out).</summary>
    public async Task<IReadOnlyList<Reconciliation>> GetHistoryAsync(int accountId, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.Reconciliations.AsNoTracking()
            .Where(r => r.AccountId == accountId && !r.IsUndone)
            .OrderByDescending(r => r.StatementDate).ThenByDescending(r => r.Id)
            .ToListAsync(ct);
    }

    /// <summary>Undoes the latest reconciliation of the account: its transactions go back to Cleared.</summary>
    public async Task UndoLastAsync(int accountId, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var last = await db.Reconciliations
            .Where(r => r.AccountId == accountId && !r.IsUndone)
            .OrderByDescending(r => r.StatementDate).ThenByDescending(r => r.Id)
            .FirstOrDefaultAsync(ct)
            ?? throw new LedgerValidationException("There's no reconciliation to undo.");
        foreach (var t in await db.Transactions.IgnoreQueryFilters().Where(t => t.ReconciliationId == last.Id).ToListAsync(ct))
        {
            t.Status = TransactionStatus.Cleared;
            t.ReconciliationId = null;
        }
        last.IsUndone = true;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>A transaction's effect on one account: + for money in, − for money out (a transfer is out of its source, into its destination).</summary>
    internal static long EffectOn(int accountId, TransactionType type, long amount, int fromAccountId, int? toAccountId) => type switch
    {
        TransactionType.Income or TransactionType.Refund or TransactionType.OutsideIn => amount,
        TransactionType.Expense or TransactionType.OutsideOut => -amount,
        _ when toAccountId == accountId => amount,
        _ => -amount,
    };

    private static string KindName(TransactionType type) => type switch
    {
        TransactionType.Income => "Income",
        TransactionType.Expense => "Spending",
        TransactionType.Refund => "Refund",
        _ => "Transfer",
    };

    /// <summary>Every amount made by swapping two neighbouring digits of <paramref name="cents"/> (e.g. 4510 → 5410, 4150, 4501).</summary>
    internal static IEnumerable<long> AdjacentSwaps(long cents)
    {
        var digits = cents.ToString(System.Globalization.CultureInfo.InvariantCulture).ToCharArray();
        for (var i = 0; i + 1 < digits.Length; i++)
        {
            if (digits[i] == digits[i + 1]) continue;
            var copy = (char[])digits.Clone();
            (copy[i], copy[i + 1]) = (copy[i + 1], copy[i]);
            if (copy[0] == '0') continue;
            yield return long.Parse(new string(copy), System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}

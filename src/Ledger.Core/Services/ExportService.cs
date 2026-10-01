using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ledger.Core.Data;
using Ledger.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Core.Services;

/// <summary>
/// Exports data in open formats so the user is never locked in: transactions as CSV for
/// spreadsheets, and everything as versioned JSON (documented in docs/EXPORT_FORMAT.md).
/// </summary>
public sealed class ExportService(IDbContextFactory<LedgerDbContext> contextFactory, TimeProvider time)
{
    /// <summary>Bump when the JSON structure changes incompatibly, and update docs/EXPORT_FORMAT.md.</summary>
    public const int JsonFormatVersion = 3;
    public const string JsonFormatName = "ledger-export";

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static readonly string[] CsvColumns =
    [
        "Date", "Type", "Payee", "Category", "Account", "To account",
        "Amount", "Signed amount", "Status", "Note", "Recorded automatically",
    ];

    // ---- CSV ----------------------------------------------------------------------------------

    /// <summary>
    /// Every non-deleted transaction, oldest first, as RFC 4180 CSV with a UTF-8 byte-order mark
    /// (so Excel reads accents correctly). Amounts use a dot and two decimals. "Signed amount" is
    /// positive for income, negative for spending, and blank for transfers (they move money, they
    /// don't gain or lose it).
    /// </summary>
    public async Task WriteTransactionsCsvAsync(Stream output, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var rows = await db.Transactions.AsNoTracking()
            .OrderBy(t => t.Date).ThenBy(t => t.Id)
            .Select(t => new
            {
                t.Date,
                t.TransactionType,
                t.Payee,
                Category = t.Category == null ? null
                    : t.Category.ParentCategory == null ? t.Category.Name
                    : t.Category.ParentCategory.Name + " › " + t.Category.Name,
                Account = t.Account!.Name,
                To = t.TransferAccount == null ? null : t.TransferAccount.Name,
                t.AmountCents,
                t.Status,
                t.Memo,
                t.IsAutoRecorded,
            })
            .ToListAsync(ct);

        await using var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), leaveOpen: true);
        writer.NewLine = "\r\n";
        await writer.WriteLineAsync(string.Join(",", CsvColumns.Select(Csv)));
        foreach (var r in rows)
        {
            var signed = r.TransactionType switch
            {
                TransactionType.Income or TransactionType.Refund or TransactionType.OutsideIn => Decimal(r.AmountCents),
                TransactionType.Expense or TransactionType.OutsideOut => Decimal(-r.AmountCents),
                _ => "",
            };
            string[] cells =
            [
                r.Date.ToString("yyyy-MM-dd", Invariant),
                r.TransactionType switch { TransactionType.Income => "Income", TransactionType.Expense => "Spending", TransactionType.Refund => "Refund", TransactionType.OutsideIn => "Transfer in (outside)", TransactionType.OutsideOut => "Transfer out (outside)", _ => "Transfer" },
                Text(r.Payee),
                Text(r.Category),
                Text(r.Account),
                Text(r.To),
                Decimal(r.AmountCents),
                signed,
                r.Status.ToString(),
                Text(r.Memo),
                r.IsAutoRecorded ? "Yes" : "No",
            ];
            await writer.WriteLineAsync(string.Join(",", cells.Select(Csv)));
        }
    }

    private static string Decimal(long cents) => (cents / 100m).ToString("0.00", Invariant);

    /// <summary>
    /// Text a spreadsheet would treat as a formula (starting with = + - @ or a control character)
    /// gets a leading apostrophe, so an imported payee like "=HYPERLINK(…)" can never run.
    /// </summary>
    internal static string Text(string? value) =>
        string.IsNullOrEmpty(value) ? "" : value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + value : value;

    /// <summary>RFC 4180: quote when needed, doubling any quotes.</summary>
    internal static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

    // ---- JSON ---------------------------------------------------------------------------------

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // readable "›" and "’" in a file, not HTML
    };

    /// <summary>
    /// Everything Ledger stores, as one JSON document. Money is integer cents (fields ending in
    /// "Cents"), dates are ISO 8601, and records refer to each other by id. Deleted transactions are
    /// included with "isDeleted": true so the export is complete.
    /// </summary>
    public async Task WriteJsonAsync(Stream output, string appVersion, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var document = new
        {
            format = JsonFormatName,
            formatVersion = JsonFormatVersion,
            exportedAt = time.GetUtcNow().UtcDateTime,
            appVersion,
            currency = "USD",
            accounts = await db.Accounts.AsNoTracking().OrderBy(a => a.Id).Select(a => new
            {
                a.Id, a.Name, type = a.AccountType, a.InstitutionName, a.StartingBalanceCents, a.BalanceDate,
                a.IsIncludedInNetWorth, a.IsArchived, a.SortOrder, a.CreatedAt, a.UpdatedAt,
            }).ToListAsync(ct),
            categories = await db.Categories.AsNoTracking().OrderBy(c => c.Id).Select(c => new
            {
                c.Id, c.Name, c.ParentCategoryId, type = c.CategoryType, c.Icon, c.IsArchived, c.SortOrder,
            }).ToListAsync(ct),
            transactions = await db.Transactions.IgnoreQueryFilters().AsNoTracking().OrderBy(t => t.Id).Select(t => new
            {
                t.Id, t.Date, type = t.TransactionType, t.AmountCents, t.AccountId, t.TransferAccountId, t.CategoryId,
                t.Payee, t.Memo, t.Status, t.RecurringTransactionId, t.RecurrenceDate, t.IsAutoRecorded,
                t.ExternalId, t.ImportBatchId, t.ReconciliationId, t.PaidFromGoalId, t.IsDeleted, t.DeletedAt, t.CreatedAt, t.UpdatedAt,
            }).ToListAsync(ct),
            budgets = await db.Budgets.AsNoTracking().OrderBy(b => b.Year).ThenBy(b => b.Month).Select(b => new
            {
                b.Id, b.Year, b.Month,
                items = b.Items.OrderBy(i => i.CategoryId).Select(i => new { i.CategoryId, i.PlannedAmountCents }).ToList(),
            }).ToListAsync(ct),
            recurring = await db.RecurringTransactions.AsNoTracking().OrderBy(r => r.Id).Select(r => new
            {
                r.Id, r.Name, type = r.TransactionType, r.AmountCents, r.IsAmountEstimated, r.Frequency, r.StartDate, r.EndDate,
                r.AccountId, r.TransferAccountId, r.CategoryId, r.Payee, r.Memo, r.IsActive, r.AutoRecordSince, r.CreatedAt, r.UpdatedAt,
            }).ToListAsync(ct),
            recurringSkips = await db.RecurringSkips.AsNoTracking().OrderBy(s => s.Id).Select(s => new
            {
                s.RecurringTransactionId, s.Date,
            }).ToListAsync(ct),
            goals = await db.Goals.AsNoTracking().OrderBy(g => g.Id).Select(g => new
            {
                g.Id, g.Name, g.TargetAmountCents, g.StartingAmountCents, g.TargetDate, g.Tracking, g.AccountId, g.IsSinkingFund, g.MonthlySetAsideCents, g.CategoryId, g.IsArchived, g.SortOrder,
                g.CreatedAt, g.UpdatedAt,
            }).ToListAsync(ct),
            goalContributions = await db.GoalContributions.AsNoTracking().OrderBy(c => c.Id).Select(c => new
            {
                c.Id, c.GoalId, c.Date, c.AmountCents, c.Note, c.CreatedAt,
            }).ToListAsync(ct),
            jobs = await db.Jobs.AsNoTracking().OrderBy(j => j.Id).Select(j => new
            {
                j.Id, j.Name, j.PayFrequency, j.PayPeriodAnchor, j.PaydayOffsetDays, j.IsPaydayConfirmed, j.WorkweekStart,
                j.IsOvertimeEligible, j.UnpaidBreakMinutes, j.UnpaidBreakAfterMinutes, j.PaidBreakMinutes,
                j.DepositAccountId, j.IncomeCategoryId, j.FilingStatus, j.W4MultipleJobs, j.W4DependentsCreditCents,
                j.W4OtherIncomeCents, j.W4DeductionsCents, j.W4ExtraWithholdingCents, j.WorkLocationName, j.WorkPsdCode,
                j.WorkNonResidentEitBasisPoints, j.WorkLstAnnualCents, j.IsLstExempt, j.IsArchived,
                rates = j.Rates.OrderBy(r => r.EffectiveDate).Select(r => new { r.EffectiveDate, r.HourlyRateCents }).ToList(),
            }).ToListAsync(ct),
            shifts = await db.Shifts.AsNoTracking().OrderBy(s => s.Date).ThenBy(s => s.Id).Select(s => new
            {
                s.Id, s.JobId, s.Date, s.ScheduledMinutes, s.StartTime, s.EndTime, s.BreakOverrideMinutes, s.Status, s.Note,
            }).ToListAsync(ct),
            paycheckRecords = await db.PaycheckRecords.IgnoreQueryFilters().AsNoTracking().OrderBy(p => p.Id).Select(p => new
            {
                p.JobId, p.PeriodStart, p.PayDate, p.EstimatedNetCents, p.TransactionId,
            }).ToListAsync(ct),
            jobDeductions = await db.JobDeductions.AsNoTracking().OrderBy(d => d.Id).Select(d => new
            {
                d.Id, d.JobId, d.Name, d.Kind, d.AmountCents, d.PercentBasisPoints, d.StartDate, d.EndDate,
            }).ToListAsync(ct),
            paystubs = await db.Paystubs.AsNoTracking().OrderBy(s => s.PayDate).Select(s => new
            {
                s.JobId, s.PeriodStart, s.StubPeriodStart, s.StubPeriodEnd, s.PayDate, s.PaidMinutes, s.GrossCents, s.FederalCents,
                s.SocialSecurityCents, s.MedicareCents, s.StateCents, s.StateUnemploymentCents, s.LocalEitCents, s.LocalServicesTaxCents, s.NetCents,
            }).ToListAsync(ct),
            importBatches = await db.ImportBatches.AsNoTracking().OrderBy(b => b.Id).Select(b => new
            {
                b.Id, b.AccountId, b.FileName, b.ImportedAt, b.TransactionCount, b.IsUndone,
            }).ToListAsync(ct),
            importProfiles = await db.ImportProfiles.AsNoTracking().OrderBy(p => p.Id).Select(p => new
            {
                p.AccountId, p.BankAccountKey, p.DateColumn, p.DescriptionColumn, p.AmountColumn, p.DebitColumn, p.CreditColumn,
                p.MemoColumn, p.ReferenceColumn, p.DateFormat, p.InvertSign,
            }).ToListAsync(ct),
            reconciliations = await db.Reconciliations.AsNoTracking().OrderBy(r => r.Id).Select(r => new
            {
                r.Id, r.AccountId, r.StatementDate, r.StatementBalanceCents, r.ReconciledAt, r.TransactionCount, r.IsUndone,
            }).ToListAsync(ct),
            importPayeeRules = await db.ImportPayeeRules.AsNoTracking().OrderBy(r => r.Key).Select(r => new
            {
                r.Key, r.Payee, r.CategoryId, r.TransferAccountId, r.IsOutsideTransfer,
            }).ToListAsync(ct),
            settings = await db.Settings.AsNoTracking().OrderBy(s => s.Key).Select(s => new { s.Key, s.Value }).ToListAsync(ct),
        };
        await JsonSerializer.SerializeAsync(output, document, JsonOptions, ct);
    }

    public string SuggestedCsvName() => $"Ledger transactions {time.GetLocalNow():yyyy-MM-dd}.csv";
    public string SuggestedJsonName() => $"Ledger export {time.GetLocalNow():yyyy-MM-dd}.json";
}

namespace Ledger.Core.Models;

/// <summary>
/// A rule describing money expected to move on a schedule (bills, subscriptions, paychecks).
/// It produces forecast events; it never creates historical transactions by itself.
/// </summary>
/// <remarks>
/// Occurrences are computed from <see cref="StartDate"/> rather than stored as a
/// mutable "next occurrence" field. This keeps the schedule deterministic, lets
/// monthly rules anchored on the 31st return to the 31st after a short month,
/// and means launching the app can never advance or duplicate anything.
/// </remarks>
public class RecurringTransaction : IAuditable
{
    public int Id { get; set; }
    public required string Name { get; set; }

    /// <summary>Positive magnitude in cents. For variable income this is an estimate.</summary>
    public long AmountCents { get; set; }

    /// <summary>True when the amount varies (e.g. hourly pay) and is only an estimate.</summary>
    public bool IsAmountEstimated { get; set; }

    public RecurrenceFrequency Frequency { get; set; }
    public TransactionType TransactionType { get; set; }

    public int AccountId { get; set; }
    public Account? Account { get; set; }

    public int? TransferAccountId { get; set; }
    public Account? TransferAccount { get; set; }

    public int? CategoryId { get; set; }
    public Category? Category { get; set; }

    public string? Payee { get; set; }
    public string? Memo { get; set; }

    /// <summary>The first occurrence; also the anchor all later occurrences are computed from.</summary>
    public DateOnly StartDate { get; set; }

    /// <summary>Inclusive last date on which an occurrence may fall.</summary>
    public DateOnly? EndDate { get; set; }

    /// <summary>
    /// When set, each date from this day on is recorded as a real transaction automatically when it
    /// comes due (fixed amounts only). Null means the user records each one. Dates before this day are
    /// never auto-recorded, so turning it on can't create a backlog of transactions.
    /// </summary>
    public DateOnly? AutoRecordSince { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

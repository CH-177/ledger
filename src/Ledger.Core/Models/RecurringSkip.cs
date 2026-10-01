namespace Ledger.Core.Models;

/// <summary>
/// One occurrence of a recurring rule that the user said won't happen (a skipped month, a
/// paused subscription). Skipped occurrences leave the forecast and never ask to be recorded.
/// </summary>
public class RecurringSkip
{
    public int Id { get; set; }

    public int RecurringTransactionId { get; set; }
    public RecurringTransaction? RecurringTransaction { get; set; }

    /// <summary>The scheduled date being skipped.</summary>
    public DateOnly Date { get; set; }
}

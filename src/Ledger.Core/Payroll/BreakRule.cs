using Ledger.Core.Models;

namespace Ledger.Core.Payroll;

/// <summary>
/// An employer's break policy: an unpaid break taken off shifts of a given length or longer,
/// plus a paid break that doesn't affect pay (shown for information).
/// </summary>
public sealed record BreakRule(int UnpaidMinutes, int UnpaidAfterMinutes, int PaidMinutes)
{
    public static BreakRule For(Job job) => new(job.UnpaidBreakMinutes, job.UnpaidBreakAfterMinutes, job.PaidBreakMinutes);

    /// <summary>No breaks at all.</summary>
    public static readonly BreakRule None = new(0, 0, 0);

    /// <summary>The unpaid break for a shift: the override if given, otherwise the rule.</summary>
    public int UnpaidBreakFor(int scheduledMinutes, int? overrideMinutes) =>
        overrideMinutes ?? (UnpaidMinutes > 0 && scheduledMinutes >= UnpaidAfterMinutes ? UnpaidMinutes : 0);

    /// <summary>Paid time for a shift: its length minus the unpaid break (never below zero).</summary>
    public int PaidMinutesFor(int scheduledMinutes, int? overrideMinutes) =>
        Math.Max(0, scheduledMinutes - UnpaidBreakFor(scheduledMinutes, overrideMinutes));
}

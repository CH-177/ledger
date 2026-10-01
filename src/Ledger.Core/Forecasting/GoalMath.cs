namespace Ledger.Core.Forecasting;

/// <summary>Planning arithmetic for savings goals. Suggestions only: nothing here moves money.</summary>
public static class GoalMath
{
    /// <summary>What's left to save; never negative.</summary>
    public static long Remaining(long targetCents, long currentCents) => Math.Max(0, targetCents - currentCents);

    /// <summary>
    /// Whole percent saved, rounded down and capped at 100, so a goal never shows 100% before it's
    /// actually reached.
    /// </summary>
    public static int Percent(long targetCents, long currentCents) =>
        targetCents <= 0 || currentCents <= 0 ? 0 : (int)Math.Min(100, currentCents * 100 / targetCents);

    /// <summary>0–1 for a progress bar.</summary>
    public static double Fraction(long targetCents, long currentCents) =>
        targetCents <= 0 ? 0 : Math.Clamp((double)currentCents / targetCents, 0, 1);

    /// <summary>
    /// How many monthly contributions fit before the target date, saving today and then on the same
    /// day each month: Sep 27 → Jun 15 allows Sep 27, Oct 27, …, May 27 = 9. Zero once the date has passed.
    /// </summary>
    public static int MonthsLeft(DateOnly today, DateOnly targetDate)
    {
        if (targetDate < today)
        {
            return 0;
        }
        var months = (targetDate.Year - today.Year) * 12 + targetDate.Month - today.Month;
        // Contribution k falls on today.AddMonths(k); count k = 0..months while it's on or before the target.
        return today.AddMonths(months) <= targetDate ? months + 1 : months;
    }

    /// <summary>
    /// The monthly amount that reaches the target by the date, rounded up to the cent so following it
    /// never leaves you short. Null when there's no date, the date has passed, or the goal is reached.
    /// </summary>
    public static long? SuggestedMonthly(long targetCents, long currentCents, DateOnly today, DateOnly? targetDate)
    {
        var remaining = Remaining(targetCents, currentCents);
        if (remaining == 0 || targetDate is not { } date)
        {
            return null;
        }
        var months = MonthsLeft(today, date);
        return months == 0 ? null : (remaining + months - 1) / months;
    }
}

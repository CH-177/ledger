using Ledger.Core.Models;

namespace Ledger.Core.Forecasting;

/// <summary>
/// Dates on which a recurring rule falls. Every occurrence is computed from the start date
/// (the nth occurrence is start + n periods), never from the previous occurrence, so a monthly
/// bill on the 31st falls on Feb 28 and returns to Mar 31 rather than drifting to the 28th.
/// </summary>
public static class RecurrenceSchedule
{
    /// <summary>The occurrence <paramref name="n"/> periods after <paramref name="start"/> (n = 0 is the start).</summary>
    public static DateOnly Nth(DateOnly start, RecurrenceFrequency frequency, int n) => frequency switch
    {
        RecurrenceFrequency.Weekly => start.AddDays(7 * n),
        RecurrenceFrequency.Biweekly => start.AddDays(14 * n),
        RecurrenceFrequency.Monthly => start.AddMonths(n),
        RecurrenceFrequency.Quarterly => start.AddMonths(3 * n),
        RecurrenceFrequency.Yearly => start.AddYears(n),
        _ => throw new ArgumentOutOfRangeException(nameof(frequency)),
    };

    /// <summary>
    /// Occurrences from <paramref name="from"/> to <paramref name="to"/> inclusive, never before
    /// <paramref name="start"/> or after <paramref name="end"/>.
    /// </summary>
    public static IEnumerable<DateOnly> Between(DateOnly start, DateOnly? end, RecurrenceFrequency frequency, DateOnly from, DateOnly to)
    {
        var last = end is { } e && e < to ? e : to;
        if (last < from || last < start)
        {
            yield break;
        }

        for (var n = FirstIndexOnOrAfter(start, frequency, from); ; n++)
        {
            var date = Nth(start, frequency, n);
            if (date > last)
            {
                yield break;
            }
            yield return date;
        }
    }

    /// <summary>True when <paramref name="date"/> is one of the rule's scheduled dates.</summary>
    public static bool IsOccurrence(DateOnly start, DateOnly? end, RecurrenceFrequency frequency, DateOnly date) =>
        Between(start, end, frequency, date, date).Any();

    /// <summary>
    /// The same amount expressed per month, for comparing bills on different schedules
    /// (weekly × 52 ÷ 12, every two weeks × 26 ÷ 12, quarterly ÷ 3, yearly ÷ 12).
    /// </summary>
    public static long MonthlyEquivalent(long cents, RecurrenceFrequency frequency) => frequency switch
    {
        RecurrenceFrequency.Weekly => Money.RoundToCents(cents * 52m / 12m),
        RecurrenceFrequency.Biweekly => Money.RoundToCents(cents * 26m / 12m),
        RecurrenceFrequency.Monthly => cents,
        RecurrenceFrequency.Quarterly => Money.RoundToCents(cents / 3m),
        RecurrenceFrequency.Yearly => Money.RoundToCents(cents / 12m),
        _ => throw new ArgumentOutOfRangeException(nameof(frequency)),
    };

    /// <summary>The smallest n whose occurrence is on or after <paramref name="from"/>.</summary>
    private static int FirstIndexOnOrAfter(DateOnly start, RecurrenceFrequency frequency, DateOnly from)
    {
        if (from <= start)
        {
            return 0;
        }

        var days = from.DayNumber - start.DayNumber;
        var months = (from.Year - start.Year) * 12 + from.Month - start.Month;
        var n = frequency switch
        {
            RecurrenceFrequency.Weekly => days / 7,
            RecurrenceFrequency.Biweekly => days / 14,
            RecurrenceFrequency.Monthly => months,
            RecurrenceFrequency.Quarterly => months / 3,
            _ => months / 12,
        };

        // The estimate can be one period off either way around month ends; settle it exactly.
        n = Math.Max(0, n - 1);
        while (Nth(start, frequency, n) < from)
        {
            n++;
        }
        return n;
    }
}

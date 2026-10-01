using Ledger.Core.Models;

namespace Ledger.Core.Payroll;

/// <summary>A pay period (inclusive dates) and the day it is paid.</summary>
public sealed record PayPeriod(DateOnly Start, DateOnly End, DateOnly PayDate);

/// <summary>Works out pay periods and paydays from a job's pay schedule.</summary>
public static class PayPeriodCalendar
{
    /// <summary>Pay periods per year as used by IRS withholding tables (Pub 15-T Table 3).</summary>
    public static int PeriodsPerYear(PayFrequency frequency) => frequency switch
    {
        PayFrequency.Weekly => 52,
        PayFrequency.Biweekly => 26,
        PayFrequency.Semimonthly => 24,
        PayFrequency.Monthly => 12,
        _ => throw new ArgumentOutOfRangeException(nameof(frequency)),
    };

    /// <summary>
    /// The pay period containing <paramref name="date"/>. Weekly and biweekly periods repeat
    /// from <paramref name="anchor"/> (any real period start, before or after the date).
    /// Semimonthly periods are the 1st–15th and 16th–end; monthly periods are calendar months.
    /// </summary>
    public static PayPeriod PeriodContaining(DateOnly date, PayFrequency frequency, DateOnly anchor, int paydayOffsetDays)
    {
        DateOnly start, end;
        switch (frequency)
        {
            case PayFrequency.Weekly:
            case PayFrequency.Biweekly:
                var length = frequency == PayFrequency.Weekly ? 7 : 14;
                var offset = Mod(date.DayNumber - anchor.DayNumber, length);
                start = date.AddDays(-offset);
                end = start.AddDays(length - 1);
                break;
            case PayFrequency.Semimonthly:
                start = date.Day <= 15 ? new DateOnly(date.Year, date.Month, 1) : new DateOnly(date.Year, date.Month, 16);
                end = date.Day <= 15 ? new DateOnly(date.Year, date.Month, 15) : new DateOnly(date.Year, date.Month, 1).AddMonths(1).AddDays(-1);
                break;
            case PayFrequency.Monthly:
                start = new DateOnly(date.Year, date.Month, 1);
                end = start.AddMonths(1).AddDays(-1);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(frequency));
        }
        return new PayPeriod(start, end, end.AddDays(paydayOffsetDays));
    }

    /// <summary>The first day of the 7-day workweek containing <paramref name="date"/>.</summary>
    public static DateOnly WorkweekStart(DateOnly date, DayOfWeek weekStartsOn) =>
        date.AddDays(-Mod((int)date.DayOfWeek - (int)weekStartsOn, 7));

    private static int Mod(int value, int divisor) => ((value % divisor) + divisor) % divisor;
}

using Ledger.Core.Models;

namespace Ledger.Core.Payroll;

/// <summary>Minimal shift facts needed to price hours. Order within a day is by start time.</summary>
public sealed record WorkedTime(DateOnly Date, int PaidMinutes, TimeOnly? StartTime = null);

/// <summary>An hourly rate effective from a date until the next rate.</summary>
public sealed record RateChange(DateOnly EffectiveDate, long HourlyRateCents);

/// <summary>A shift's minutes split into straight time and overtime, priced at its date's rate.</summary>
public sealed record PricedShift(DateOnly Date, int RegularMinutes, int OvertimeMinutes, long HourlyRateCents)
{
    public decimal Earnings(decimal overtimeMultiplier) =>
        (RegularMinutes * (decimal)HourlyRateCents + OvertimeMinutes * (decimal)HourlyRateCents * overtimeMultiplier) / 60m;
}

/// <summary>
/// Prices shifts. Overtime (1.5×) applies to minutes beyond 40 hours in the employer's fixed
/// 7-day workweek, counted in date and start-time order, as required by the FLSA and the
/// PA Minimum Wage Act. Overtime is decided per workweek, never per pay period.
/// </summary>
public static class GrossPayCalculator
{
    public const int OvertimeThresholdMinutes = 40 * 60;
    public const decimal OvertimeMultiplier = 1.5m;

    public static IReadOnlyList<PricedShift> Price(
        IEnumerable<WorkedTime> shifts,
        IReadOnlyList<RateChange> rates,
        DayOfWeek workweekStart,
        bool overtimeEligible)
    {
        var ordered = rates.OrderBy(r => r.EffectiveDate).ToList();
        var result = new List<PricedShift>();

        foreach (var week in shifts
            .OrderBy(s => s.Date).ThenBy(s => s.StartTime ?? TimeOnly.MinValue)
            .GroupBy(s => PayPeriodCalendar.WorkweekStart(s.Date, workweekStart)))
        {
            var minutesSoFar = 0;
            foreach (var shift in week)
            {
                var regular = overtimeEligible
                    ? Math.Clamp(OvertimeThresholdMinutes - minutesSoFar, 0, shift.PaidMinutes)
                    : shift.PaidMinutes;
                minutesSoFar += shift.PaidMinutes;
                result.Add(new PricedShift(shift.Date, regular, shift.PaidMinutes - regular, RateOn(shift.Date, ordered)));
            }
        }
        return result;
    }

    /// <summary>
    /// The rate effective on <paramref name="date"/>. Dates before the first rate use the
    /// first rate, since a job always has a rate for the hours it records.
    /// </summary>
    public static long RateOn(DateOnly date, IReadOnlyList<RateChange> orderedRates)
    {
        if (orderedRates.Count == 0)
        {
            throw new LedgerValidationException("Add an hourly rate for this job.");
        }
        var rate = orderedRates[0].HourlyRateCents;
        foreach (var r in orderedRates)
        {
            if (r.EffectiveDate > date) break;
            rate = r.HourlyRateCents;
        }
        return rate;
    }

    /// <summary>Total gross in cents for a set of priced shifts, rounded once at the end.</summary>
    public static long Gross(IEnumerable<PricedShift> priced) =>
        Money.RoundToCents(priced.Sum(p => p.Earnings(OvertimeMultiplier)));
}

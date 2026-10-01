using Ledger.Core.Models;
using Ledger.Core.Payroll;

namespace Ledger.Core.Tests;

public class PayScheduleTests
{
    private static readonly DateOnly Sunday = new(2026, 9, 27);

    // ---- Pay periods --------------------------------------------------------------------------

    [Fact]
    public void Weekly_sunday_to_saturday_paid_the_following_friday()
    {
        var p = PayPeriodCalendar.PeriodContaining(new DateOnly(2026, 10, 2), PayFrequency.Weekly, Sunday, 6);
        Assert.Equal(new PayPeriod(new(2026, 9, 27), new(2026, 10, 3), new(2026, 10, 9)), p);
        Assert.Equal(DayOfWeek.Friday, p.PayDate.DayOfWeek);
    }

    [Fact]
    public void Anchor_can_be_before_or_after_the_date()
    {
        var before = PayPeriodCalendar.PeriodContaining(new(2026, 9, 24), PayFrequency.Weekly, Sunday, 6);
        var after = PayPeriodCalendar.PeriodContaining(new(2026, 9, 24), PayFrequency.Weekly, Sunday.AddDays(70), 6);
        Assert.Equal(new DateOnly(2026, 9, 20), before.Start);
        Assert.Equal(before, after);
    }

    [Fact]
    public void Biweekly_periods_repeat_every_fourteen_days()
    {
        var a = PayPeriodCalendar.PeriodContaining(new(2026, 10, 9), PayFrequency.Biweekly, Sunday, 6);
        Assert.Equal((new DateOnly(2026, 9, 27), new DateOnly(2026, 10, 10)), (a.Start, a.End));
        var b = PayPeriodCalendar.PeriodContaining(new(2026, 10, 11), PayFrequency.Biweekly, Sunday, 6);
        Assert.Equal(new DateOnly(2026, 10, 11), b.Start);
    }

    [Theory]
    [InlineData(2026, 2, 15, 1, 15)]
    [InlineData(2026, 2, 16, 16, 28)]
    [InlineData(2028, 2, 29, 16, 29)]   // leap year
    [InlineData(2026, 12, 31, 16, 31)]
    public void Semimonthly_is_first_to_fifteenth_and_sixteenth_to_month_end(int y, int m, int d, int startDay, int endDay)
    {
        var p = PayPeriodCalendar.PeriodContaining(new(y, m, d), PayFrequency.Semimonthly, default, 5);
        Assert.Equal((startDay, endDay), (p.Start.Day, p.End.Day));
        Assert.Equal(p.End.AddDays(5), p.PayDate);
    }

    [Fact]
    public void Monthly_is_the_calendar_month_and_paydays_can_cross_the_year()
    {
        var p = PayPeriodCalendar.PeriodContaining(new(2026, 12, 10), PayFrequency.Monthly, default, 3);
        Assert.Equal((new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 31), new DateOnly(2027, 1, 3)), (p.Start, p.End, p.PayDate));
    }

    [Theory]
    [InlineData(PayFrequency.Weekly, 52)]
    [InlineData(PayFrequency.Biweekly, 26)]
    [InlineData(PayFrequency.Semimonthly, 24)]
    [InlineData(PayFrequency.Monthly, 12)]
    public void Periods_per_year_match_pub_15t(PayFrequency f, int n) => Assert.Equal(n, PayPeriodCalendar.PeriodsPerYear(f));

    [Theory]
    [InlineData(2026, 10, 1, DayOfWeek.Sunday, 2026, 9, 27)]
    [InlineData(2026, 9, 27, DayOfWeek.Sunday, 2026, 9, 27)]
    [InlineData(2026, 10, 1, DayOfWeek.Monday, 2026, 9, 28)]
    public void Workweek_start(int y, int m, int d, DayOfWeek startsOn, int ey, int em, int ed) =>
        Assert.Equal(new DateOnly(ey, em, ed), PayPeriodCalendar.WorkweekStart(new(y, m, d), startsOn));

    // ---- Gross pay and overtime ---------------------------------------------------------------

    private static readonly RateChange[] ThirteenFifty = [new(new DateOnly(2026, 1, 1), 1350)];

    private static IReadOnlyList<PricedShift> Price(params (int Day, int Hours)[] shifts) =>
        GrossPayCalculator.Price(shifts.Select(s => new WorkedTime(new DateOnly(2026, 10, s.Day), s.Hours * 60)), ThirteenFifty, DayOfWeek.Sunday, true);

    [Fact]
    public void Your_first_full_week_is_189_dollars()
    {
        var priced = GrossPayCalculator.Price(
            [new(new(2026, 9, 29), 240), new(new(2026, 9, 30), 240), new(new(2026, 10, 2), 360)],
            ThirteenFifty, DayOfWeek.Sunday, true);
        Assert.Equal(18900, GrossPayCalculator.Gross(priced));
    }

    [Fact]
    public void Hours_over_forty_in_a_workweek_are_time_and_a_half()
    {
        // Sun Oct 4 – Sat Oct 10: 5 × 9h = 45h → 40 regular + 5 overtime.
        var priced = Price((4, 9), (5, 9), (6, 9), (7, 9), (8, 9));
        Assert.Equal(40 * 60, priced.Sum(p => p.RegularMinutes));
        Assert.Equal(5 * 60, priced.Sum(p => p.OvertimeMinutes));
        Assert.Equal(40 * 1350 + 5 * 2025, GrossPayCalculator.Gross(priced));
        // The shift that crosses 40 hours is split: 4h regular, 5h overtime.
        Assert.Equal((240, 300), (priced[^1].RegularMinutes, priced[^1].OvertimeMinutes));
    }

    [Fact]
    public void Overtime_resets_each_workweek_even_inside_one_pay_period()
    {
        // 30h Sat Oct 10 week + 30h in the week starting Sun Oct 11: no overtime at all.
        var priced = Price((6, 10), (8, 10), (10, 10), (11, 10), (12, 10), (13, 10));
        Assert.Equal(0, priced.Sum(p => p.OvertimeMinutes));
    }

    [Fact]
    public void Overtime_not_eligible_pays_straight_time()
    {
        var priced = GrossPayCalculator.Price(
            Enumerable.Range(4, 5).Select(d => new WorkedTime(new(2026, 10, d), 9 * 60)), ThirteenFifty, DayOfWeek.Sunday, false);
        Assert.Equal(0, priced.Sum(p => p.OvertimeMinutes));
    }

    [Fact]
    public void A_raise_applies_from_its_effective_date_only()
    {
        RateChange[] rates = [new(new(2026, 1, 1), 1350), new(new(2026, 10, 5), 1400)];
        var priced = GrossPayCalculator.Price([new(new(2026, 10, 4), 60), new(new(2026, 10, 5), 60)], rates, DayOfWeek.Sunday, true);
        Assert.Equal([1350L, 1400L], priced.Select(p => p.HourlyRateCents));
        Assert.Equal(2750, GrossPayCalculator.Gross(priced));
    }

    [Fact]
    public void Fractional_hours_round_once_per_paycheck()
    {
        // 4.5h at $13.50 = $60.75 exactly; 7m at $13.50 = $1.575 → $1.58
        Assert.Equal(6075, GrossPayCalculator.Gross(GrossPayCalculator.Price([new(new(2026, 10, 7), 270)], ThirteenFifty, DayOfWeek.Sunday, true)));
        Assert.Equal(158, GrossPayCalculator.Gross(GrossPayCalculator.Price([new(new(2026, 10, 7), 7)], ThirteenFifty, DayOfWeek.Sunday, true)));
    }

    [Fact]
    public void A_job_without_a_rate_is_a_friendly_error() =>
        Assert.Throws<LedgerValidationException>(() => GrossPayCalculator.RateOn(new(2026, 1, 1), []));
}

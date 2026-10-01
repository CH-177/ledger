using Ledger.Core.Forecasting;
using Ledger.Core.Models;

namespace Ledger.Core.Tests;

public class RecurrenceScheduleTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    [Fact]
    public void Monthly_on_the_31st_uses_the_last_day_of_short_months_and_returns_to_the_31st()
    {
        var dates = RecurrenceSchedule.Between(D(2027, 1, 31), null, RecurrenceFrequency.Monthly, D(2027, 1, 1), D(2027, 5, 31));
        Assert.Equal([D(2027, 1, 31), D(2027, 2, 28), D(2027, 3, 31), D(2027, 4, 30), D(2027, 5, 31)], dates);
    }

    [Fact]
    public void Yearly_on_leap_day_falls_on_feb_28_in_other_years()
    {
        var dates = RecurrenceSchedule.Between(D(2028, 2, 29), null, RecurrenceFrequency.Yearly, D(2028, 1, 1), D(2032, 12, 31));
        Assert.Equal([D(2028, 2, 29), D(2029, 2, 28), D(2030, 2, 28), D(2031, 2, 28), D(2032, 2, 29)], dates);
    }

    [Theory]
    [InlineData(RecurrenceFrequency.Weekly, new[] { "2026-10-02", "2026-10-09", "2026-10-16", "2026-10-23", "2026-10-30" })]
    [InlineData(RecurrenceFrequency.Biweekly, new[] { "2026-10-02", "2026-10-16", "2026-10-30" })]
    [InlineData(RecurrenceFrequency.Monthly, new[] { "2026-10-02" })]
    public void Weekly_and_biweekly_step_by_exact_days(RecurrenceFrequency frequency, string[] expected)
    {
        var dates = RecurrenceSchedule.Between(D(2026, 10, 2), null, frequency, D(2026, 10, 1), D(2026, 10, 31));
        Assert.Equal(expected.Select(DateOnly.Parse), dates);
    }

    [Fact]
    public void Quarterly_steps_three_months()
    {
        var dates = RecurrenceSchedule.Between(D(2026, 1, 15), null, RecurrenceFrequency.Quarterly, D(2026, 1, 1), D(2026, 12, 31));
        Assert.Equal([D(2026, 1, 15), D(2026, 4, 15), D(2026, 7, 15), D(2026, 10, 15)], dates);
    }

    [Fact]
    public void Nothing_before_the_start_or_after_the_end()
    {
        Assert.Equal([D(2026, 10, 15), D(2026, 11, 15)],
            RecurrenceSchedule.Between(D(2026, 10, 15), D(2026, 12, 14), RecurrenceFrequency.Monthly, D(2026, 1, 1), D(2027, 12, 31)));
        Assert.Empty(RecurrenceSchedule.Between(D(2026, 10, 15), null, RecurrenceFrequency.Monthly, D(2026, 1, 1), D(2026, 10, 14)));
        Assert.Empty(RecurrenceSchedule.Between(D(2026, 10, 15), D(2026, 10, 20), RecurrenceFrequency.Weekly, D(2026, 10, 21), D(2026, 12, 1)));
    }

    [Fact]
    public void An_end_date_on_an_occurrence_includes_it()
    {
        Assert.Equal([D(2026, 10, 15), D(2026, 11, 15)],
            RecurrenceSchedule.Between(D(2026, 10, 15), D(2026, 11, 15), RecurrenceFrequency.Monthly, D(2026, 1, 1), D(2027, 1, 1)));
    }

    [Fact]
    public void Starting_mid_range_after_a_short_month_still_finds_the_31st()
    {
        Assert.Equal([D(2027, 3, 31)], RecurrenceSchedule.Between(D(2027, 1, 31), null, RecurrenceFrequency.Monthly, D(2027, 3, 1), D(2027, 3, 31)));
        Assert.Equal([D(2027, 2, 28)], RecurrenceSchedule.Between(D(2027, 1, 31), null, RecurrenceFrequency.Monthly, D(2027, 2, 28), D(2027, 2, 28)));
        Assert.True(RecurrenceSchedule.IsOccurrence(D(2027, 1, 31), null, RecurrenceFrequency.Monthly, D(2027, 4, 30)));
        Assert.False(RecurrenceSchedule.IsOccurrence(D(2027, 1, 31), null, RecurrenceFrequency.Monthly, D(2027, 2, 27)));
    }

    /// <summary>The fast path (jumping to the first index in range) must agree with walking from the start.</summary>
    [Fact]
    public void Any_window_matches_walking_every_occurrence_from_the_start()
    {
        var random = new Random(20260927);
        for (var i = 0; i < 2000; i++)
        {
            var frequency = (RecurrenceFrequency)random.Next(5);
            var start = D(2024, 1, 1).AddDays(random.Next(1500));
            var from = start.AddDays(random.Next(-60, 1200));
            var to = from.AddDays(random.Next(0, 400));
            DateOnly? end = random.Next(3) == 0 ? start.AddDays(random.Next(0, 1500)) : null;

            var walked = Enumerable.Range(0, 400)
                .Select(n => RecurrenceSchedule.Nth(start, frequency, n))
                .Where(d => d >= from && d <= to && (end is null || d <= end))
                .ToList();
            Assert.Equal(walked, RecurrenceSchedule.Between(start, end, frequency, from, to));
        }
    }

    [Theory]
    [InlineData(RecurrenceFrequency.Weekly, 1000, 4333)]      // 10 × 52 ÷ 12
    [InlineData(RecurrenceFrequency.Biweekly, 1000, 2167)]    // 10 × 26 ÷ 12
    [InlineData(RecurrenceFrequency.Monthly, 3790, 3790)]
    [InlineData(RecurrenceFrequency.Quarterly, 1000, 333)]
    [InlineData(RecurrenceFrequency.Yearly, 12000, 1000)]
    public void Monthly_equivalent(RecurrenceFrequency frequency, long cents, long expected) =>
        Assert.Equal(expected, RecurrenceSchedule.MonthlyEquivalent(cents, frequency));
}

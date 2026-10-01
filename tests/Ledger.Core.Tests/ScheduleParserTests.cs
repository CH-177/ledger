using Ledger.Core.Payroll;

namespace Ledger.Core.Tests;

public class ScheduleParserTests
{
    private static readonly DateOnly Today = new(2026, 9, 27);

    private static (DateOnly Date, double Hours)[] Shifts(ScheduleParseResult r) =>
        r.Shifts.Select(s => (s.Date, s.ScheduledMinutes / 60.0)).ToArray();

    [Fact]
    public void Your_schedule_text_exactly_as_written()
    {
        var r = ScheduleParser.Parse("29th: 4hrs, 30th: 4hrs, Oct. 2nd: 6hrs, 7th: 4.5hrs, 9th: 7hrs, 10th: 8hrs", Today);

        Assert.Empty(r.Unreadable);
        Assert.Equal(
        [
            (new DateOnly(2026, 9, 29), 4.0),
            (new DateOnly(2026, 9, 30), 4.0),
            (new DateOnly(2026, 10, 2), 6.0),
            (new DateOnly(2026, 10, 7), 4.5),
            (new DateOnly(2026, 10, 9), 7.0),
            (new DateOnly(2026, 10, 10), 8.0),
        ], Shifts(r));
    }

    [Fact]
    public void One_shift_per_line_with_times_and_meal_break()
    {
        var r = ScheduleParser.Parse(
            """
            Tue 9/29 4:00 PM - 8:00 PM
            Fri 10/2 11:00 AM - 5:30 PM (30 min meal)
            Sat 10/3 OFF
            """, Today);

        // Shift lengths as scheduled; the meal break is recorded separately.
        Assert.Equal([(new DateOnly(2026, 9, 29), 4.0), (new DateOnly(2026, 10, 2), 6.5)], Shifts(r));
        Assert.Equal(new TimeOnly(16, 0), r.Shifts[0].Start);
        Assert.Equal(30, r.Shifts[1].BreakMinutes);
        Assert.Single(r.DaysOff);
    }

    [Fact]
    public void Date_and_times_on_separate_lines()
    {
        var r = ScheduleParser.Parse(
            """
            Tuesday, September 29
            4:00 PM - 8:00 PM
            Friday, October 2
            11:00 AM - 5:00 PM
            """, Today);

        Assert.Empty(r.Unreadable);
        Assert.Equal([(new DateOnly(2026, 9, 29), 4.0), (new DateOnly(2026, 10, 2), 6.0)], Shifts(r));
    }

    [Theory]
    [InlineData("10/7 4p-9:30p", 5.5, 16, 0)]
    [InlineData("10/7 16:00-21:30", 5.5, 16, 0)]
    [InlineData("10/7 10-2pm", 4.0, 10, 0)]          // 10am to 2pm
    [InlineData("10/7 4-9", 5.0, 16, 0)]             // no am/pm: a retail afternoon shift
    [InlineData("10/7 9-5", 8.0, 9, 0)]              // 9am to 5pm
    [InlineData("10/7 10:00 PM - 6:00 AM", 8.0, 22, 0)] // overnight
    [InlineData("10/7 5:00pm to 9:00pm", 4.0, 17, 0)]
    public void Time_range_formats(string text, double hours, int startHour, int startMinute)
    {
        var s = Assert.Single(ScheduleParser.Parse(text, Today).Shifts);
        Assert.Equal(hours, s.ScheduledMinutes / 60.0);
        Assert.Equal(new TimeOnly(startHour, startMinute), s.Start);
    }

    [Theory]
    [InlineData("Oct 2, 2026: 6 hours")]
    [InlineData("October 2nd - 6h")]
    [InlineData("10/2/2026 6hrs")]
    [InlineData("10/02/26 6 hrs")]
    [InlineData("the 2nd: 6:00 hrs")]
    public void Date_formats(string text)
    {
        var today = new DateOnly(2026, 9, 30);
        var s = Assert.Single(ScheduleParser.Parse(text, today).Shifts);
        Assert.Equal((new DateOnly(2026, 10, 2), 360), (s.Date, s.ScheduledMinutes));
    }

    [Fact]
    public void Only_breaks_stated_in_the_text_are_captured_the_job_rule_handles_the_rest()
    {
        var r = ScheduleParser.Parse("10/7 9:00 AM - 3:00 PM\n10/8: 6hrs\n10/9 9:00 AM - 3:00 PM (45 min lunch)", Today);
        Assert.Equal([360, 360, 360], r.Shifts.Select(s => s.ScheduledMinutes));
        Assert.Equal([null, null, 45], r.Shifts.Select(s => s.BreakMinutes));
    }

    [Fact]
    public void A_stated_break_as_long_as_the_shift_is_unreadable()
    {
        var r = ScheduleParser.Parse("10/7 9:00 AM - 9:30 AM (30 min break)", Today);
        Assert.Empty(r.Shifts);
        Assert.Single(r.Unreadable);
    }

    [Fact]
    public void Months_roll_over_when_bare_days_go_backwards_and_the_year_rolls_in_december()
    {
        var december = new DateOnly(2026, 12, 20);
        var r = ScheduleParser.Parse("28th: 4hrs, 30th: 5hrs, 2nd: 6hrs, 1/5: 3hrs", december);
        Assert.Equal(
            [new DateOnly(2026, 12, 28), new DateOnly(2026, 12, 30), new DateOnly(2027, 1, 2), new DateOnly(2027, 1, 5)],
            r.Shifts.Select(s => s.Date));
    }

    [Fact]
    public void Unreadable_text_is_reported_never_dropped()
    {
        var r = ScheduleParser.Parse("My schedule\n29th: 4hrs\nsomething odd\n10/7", Today);
        Assert.Single(r.Shifts);
        Assert.Equal(["My schedule", "something odd", "10/7"], r.Unreadable);
    }

    [Fact]
    public void Impossible_dates_are_unreadable()
    {
        var r = ScheduleParser.Parse("2/30: 4hrs\n13/1: 4hrs", Today);
        Assert.Empty(r.Shifts);
        Assert.Equal(2, r.Unreadable.Count);
    }

    [Fact]
    public void Weekday_only_lines_use_the_next_such_day()
    {
        var r = ScheduleParser.Parse("Tue: 4hrs\nFri: 6hrs", Today); // today is Sunday Sep 27
        Assert.Equal([new DateOnly(2026, 9, 29), new DateOnly(2026, 10, 2)], r.Shifts.Select(s => s.Date));
    }
}

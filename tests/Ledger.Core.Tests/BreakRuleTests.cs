using Ledger.Core.Payroll;

namespace Ledger.Core.Tests;

public class BreakRuleTests
{
    // The user's employer: a paid 15-minute break always, plus an unpaid 30 minutes on shifts of 5 hours or more.
    private static readonly BreakRule Market = new(UnpaidMinutes: 30, UnpaidAfterMinutes: 300, PaidMinutes: 15);

    [Theory]
    [InlineData(240, 240)]   // 4h: paid 15 min break only, full pay
    [InlineData(270, 270)]   // 4.5h
    [InlineData(299, 299)]   // just under 5h
    [InlineData(300, 270)]   // exactly 5h: "5 hours or longer" gets the unpaid half hour
    [InlineData(360, 330)]   // 6h → 5.5h paid
    [InlineData(420, 390)]
    [InlineData(480, 450)]
    public void Unpaid_half_hour_from_five_hours(int scheduled, int paid) =>
        Assert.Equal(paid, Market.PaidMinutesFor(scheduled, null));

    [Fact]
    public void The_paid_break_never_changes_pay()
    {
        var noPaidBreak = Market with { PaidMinutes = 0 };
        foreach (var minutes in new[] { 60, 240, 300, 480 })
        {
            Assert.Equal(noPaidBreak.PaidMinutesFor(minutes, null), Market.PaidMinutesFor(minutes, null));
        }
    }

    [Theory]
    [InlineData(360, 0, 360)]    // override: no break this shift
    [InlineData(360, 45, 315)]   // override: a longer break
    [InlineData(240, 30, 210)]   // override applies even under the threshold
    public void A_shift_override_replaces_the_rule(int scheduled, int overrideMinutes, int paid) =>
        Assert.Equal(paid, Market.PaidMinutesFor(scheduled, overrideMinutes));

    [Fact]
    public void No_rule_means_no_deduction_and_paid_time_is_never_negative()
    {
        Assert.Equal(480, BreakRule.None.PaidMinutesFor(480, null));
        Assert.Equal(0, Market.PaidMinutesFor(20, 30));
    }
}

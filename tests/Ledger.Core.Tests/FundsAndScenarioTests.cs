using Ledger.Core.Forecasting;
using Ledger.Core.Models;
using Ledger.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Core.Tests;

public class FundsAndScenarioTests
{
    private static readonly DateOnly Today = new(2026, 10, 1);
    private static DateOnly D(int m, int d, int y = 2026) => new(y, m, d);

    private sealed class World : IAsyncDisposable
    {
        public required TestDatabase Db { get; init; }
        public required TransactionService Transactions { get; init; }
        public required AccountService Accounts { get; init; }
        public required GoalService Goals { get; init; }
        public required RecurringService Recurring { get; init; }
        public required IncomeService Income { get; init; }
        public required IncomeForecastService Paychecks { get; init; }
        public required ForecastService Forecasts { get; init; }
        public required ScenarioService Scenarios { get; init; }
        public required Account Checking { get; init; }
        public required int Repairs { get; init; }
        public ValueTask DisposeAsync() => Db.DisposeAsync();

        public async Task<Job> MarketAsync() => await Income.CreateJobAsync(new JobInput("Riverside Market", 1350, D(9, 1), PayFrequency.Weekly, D(9, 20), 6,
            false, DayOfWeek.Sunday, true, Checking.Id, null, FilingStatus.Single, false, 0, 0, 0, 0, "Riverton City", "990201", 50, 5200, false));
    }

    private static async Task<World> CreateAsync()
    {
        var db = await TestDatabase.CreateAsync(Today);
        var accounts = new AccountService(db.Factory, db.Time);
        var recurring = new RecurringService(db.Factory, db.Time);
        var income = new IncomeService(db.Factory, new SettingsService(db.Factory));
        var paychecks = new IncomeForecastService(db.Factory, income);
        var forecasts = new ForecastService(db.Factory, accounts, recurring, paychecks);
        var goals = new GoalService(db.Factory, accounts, db.Time);
        int repairs;
        await using (var ctx = db.Context())
        {
            repairs = await ctx.Categories.Where(c => c.CategoryType == CategoryType.Expense && c.ParentCategoryId != null).Select(c => c.Id).FirstAsync();
        }
        return new World
        {
            Db = db, Transactions = new TransactionService(db.Factory, db.Time), Accounts = accounts, Goals = goals, Recurring = recurring,
            Income = income, Paychecks = paychecks, Forecasts = forecasts,
            Scenarios = new ScenarioService(forecasts, goals, new SettingsService(db.Factory)),
            Checking = await db.AddAccountAsync("Checking", AccountType.Checking, 50000),
            Repairs = repairs,
        };
    }

    // ---- Sinking funds ----------------------------------------------------------------------------

    [Fact]
    public async Task A_fund_is_set_aside_monthly_spent_from_and_never_goes_below_zero()
    {
        await using var w = await CreateAsync();
        var fund = await w.Goals.CreateAsync(new GoalInput("Car repairs", 60000, IsSinkingFund: true, MonthlySetAsideCents: 5000, CategoryId: w.Repairs));
        await w.Goals.SetAsideMonthlyAsync(fund.Id);
        Assert.Equal(5000, (await w.Goals.GetAsync(fund.Id)).CurrentCents);
        Assert.Equal(5000, (await w.Goals.GetSetAsideInMonthAsync(2026, 10))[fund.Id]);

        // Paying from the fund lowers it; it's still spending, and the account balance moves as usual.
        var oil = await w.Transactions.CreateAsync(new TransactionInput(TransactionType.Expense, 3000, Today, w.Checking.Id, CategoryId: w.Repairs, Payee: "Oil change", PaidFromGoalId: fund.Id));
        Assert.Equal(2000, (await w.Goals.GetAsync(fund.Id)).CurrentCents);
        Assert.Equal(3000, (await w.Transactions.GetTotalsAsync(new TransactionQuery())).ExpenseCents);
        Assert.Equal(47000, await w.Accounts.GetBalanceAsync(w.Checking.Id));

        var tooMuch = await Assert.ThrowsAsync<LedgerValidationException>(() => w.Transactions.CreateAsync(
            new TransactionInput(TransactionType.Expense, 2500, Today, w.Checking.Id, PaidFromGoalId: fund.Id)));
        Assert.Contains("$20.00 set aside", tooMuch.Message);
        await Assert.ThrowsAsync<LedgerValidationException>(() => w.Goals.AddContributionAsync(fund.Id, -2500, Today));

        // Editing that spending gives its own amount back first; deleting it restores the fund.
        await w.Transactions.UpdateAsync(oil.Id, new TransactionInput(TransactionType.Expense, 5000, Today, w.Checking.Id, CategoryId: w.Repairs, Payee: "Oil change", PaidFromGoalId: fund.Id));
        Assert.Equal(0, (await w.Goals.GetAsync(fund.Id)).CurrentCents);
        await w.Transactions.DeleteAsync(oil.Id);
        Assert.Equal(5000, (await w.Goals.GetAsync(fund.Id)).CurrentCents);

        // Deleting the fund leaves the spending as plain spending.
        await w.Transactions.RestoreAsync(oil.Id);
        await w.Goals.DeleteAsync(fund.Id);
        Assert.Null((await w.Transactions.GetAsync(oil.Id)).PaidFromGoalId);
    }

    [Fact]
    public async Task A_fund_tracks_its_own_money_and_pays_for_spending_only()
    {
        await using var w = await CreateAsync();
        await Assert.ThrowsAsync<LedgerValidationException>(() => w.Goals.CreateAsync(
            new GoalInput("Gifts", 20000, Tracking: GoalTracking.AccountBalance, AccountId: w.Checking.Id, IsSinkingFund: true)));
        var fund = await w.Goals.CreateAsync(new GoalInput("Gifts", 20000, StartingAmountCents: 5000, IsSinkingFund: true));
        var income = await w.Transactions.CreateAsync(new TransactionInput(TransactionType.Income, 1000, Today, w.Checking.Id, PaidFromGoalId: fund.Id));
        Assert.Null((await w.Transactions.GetAsync(income.Id)).PaidFromGoalId); // only spending is paid from a fund
        Assert.Equal(5000, (await w.Goals.GetFundsAsync()).Single().CurrentCents);
    }

    // ---- Scenarios ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_purchase_scenario_changes_only_the_scenario_forecast_and_can_i_afford_it_says_so_plainly()
    {
        await using var w = await CreateAsync();
        await w.Recurring.CreateAsync(new RecurringInput("Phone", TransactionType.Expense, 4000, RecurrenceFrequency.Monthly, D(10, 20), w.Checking.Id));
        var laptop = new Scenario("Laptop", [new ScenarioChange(ScenarioChangeKind.Purchase, D(10, 10), 45000, w.Checking.Id, Name: "Laptop")]);

        var compare = await w.Scenarios.CompareAsync(Today, 30, laptop, cushionCents: 10000);
        Assert.Equal((46000L, D(10, 20)), (compare.Before.LowestCents, compare.Before.LowestDate));      // 500 − 40
        Assert.Equal((1000L, D(10, 20)), (compare.After.LowestCents, compare.After.LowestDate));   // 500 − 450 − 40
        Assert.Equal((D(10, 10), (DateOnly?)null), (compare.After.BelowCushionOn!.Value, compare.After.BelowZeroOn));
        Assert.Contains(compare.WithScenario.Events, e => e.Kind == ForecastEventKind.Scenario && e.Name == "Laptop");
        Assert.DoesNotContain(compare.Baseline.Events, e => e.Kind == ForecastEventKind.Scenario);

        var yes = await w.Scenarios.CanIAffordAsync(Today, 20000, D(10, 10), w.Checking.Id);
        Assert.True(yes.Affordable);
        Assert.StartsWith("Yes.", yes.Answer);
        var no = await w.Scenarios.CanIAffordAsync(Today, 48000, D(10, 10), w.Checking.Id);
        Assert.False(no.Affordable);
        Assert.Contains("below zero on Oct 20", no.Answer);
        var cushion = await w.Scenarios.CanIAffordAsync(Today, 38000, D(10, 10), w.Checking.Id, cushionCents: 10000); // $80 left after the phone
        Assert.Contains("cushion", cushion.Answer);
    }

    [Fact]
    public async Task When_the_balance_is_already_going_below_zero_the_answer_says_so_instead_of_blaming_the_purchase()
    {
        await using var w = await CreateAsync();
        await w.Recurring.CreateAsync(new RecurringInput("Rent", TransactionType.Expense, 60000, RecurrenceFrequency.Monthly, D(10, 15), w.Checking.Id));
        var answer = await w.Scenarios.CanIAffordAsync(Today, 1000, D(10, 5), w.Checking.Id);
        Assert.False(answer.Affordable);
        Assert.StartsWith("Not right now: even without it, your balance is forecast to go below $0 on Oct 15.", answer.Answer);
    }

    [Fact]
    public async Task Bills_can_be_stopped_or_added_from_a_date()
    {
        await using var w = await CreateAsync();
        var phone = await w.Recurring.CreateAsync(new RecurringInput("Phone", TransactionType.Expense, 4000, RecurrenceFrequency.Monthly, D(10, 20), w.Checking.Id));
        var scenario = new Scenario("Switch", [
            new ScenarioChange(ScenarioChangeKind.StopRecurring, D(11, 1), RecurringId: phone.Id),
            new ScenarioChange(ScenarioChangeKind.AddBill, D(11, 5), 2500, w.Checking.Id, Frequency: RecurrenceFrequency.Monthly, Name: "Cheaper phone"),
        ]);
        var f = await w.Forecasts.GetForecastAsync(Today, 75, null, scenario);
        Assert.Equal([("Phone", D(10, 20)), ("Cheaper phone", D(11, 5)), ("Cheaper phone", D(12, 5))], f.Events.Select(e => (e.Name, e.Date)));
    }

    [Fact]
    public async Task Hours_and_pay_changes_re_price_paychecks_with_taxes_and_overtime()
    {
        await using var w = await CreateAsync();
        var job = await w.MarketAsync();
        foreach (var day in new[] { 4, 5, 6, 7 }) // week of Oct 4: four 4-hour shifts, paid Oct 16
        {
            await w.Income.AddShiftAsync(new ShiftInput(job.Id, D(10, day), 240, null, null, null, ShiftStatus.Scheduled, null));
        }
        PredictedPaycheck Week(IReadOnlyList<PredictedPaycheck> list) => list.Single(p => p.Period.PayDate == D(10, 16));

        var baseline = Week(await w.Paychecks.GetPaychecksAsync(Today, D(10, 31)));
        Assert.Equal(16 * 1350, baseline.GrossCents);

        var half = Week(await w.Paychecks.GetPaychecksAsync(Today, D(10, 31), new Scenario("", [new ScenarioChange(ScenarioChangeKind.HoursPercent, Today, Percent: 50)])));
        Assert.Equal(8 * 1350, half.GrossCents);
        Assert.True(half.NetCents < baseline.NetCents && half.NetCents > 0);

        // 45 paid hours a week: 5 of them at time and a half. Taxes come from the payroll calculator.
        var fortyFive = Week(await w.Paychecks.GetPaychecksAsync(Today, D(10, 31), new Scenario("", [new ScenarioChange(ScenarioChangeKind.WeeklyHours, D(10, 4), WeeklyMinutes: 45 * 60)])));
        Assert.Equal(40 * 1350 + 5 * 2025, fortyFive.GrossCents);
        Assert.Equal(5 * 60, fortyFive.OvertimeMinutes);

        var raise = Week(await w.Paychecks.GetPaychecksAsync(Today, D(10, 31), new Scenario("", [new ScenarioChange(ScenarioChangeKind.PayRate, Today, 1500, JobId: job.Id)])));
        Assert.Equal(16 * 1500, raise.GrossCents);
    }

    [Fact]
    public async Task A_scenario_shows_how_an_account_backed_goal_is_affected_and_saved_scenarios_come_back()
    {
        await using var w = await CreateAsync();
        var savings = await w.Db.AddAccountAsync("Savings", AccountType.Savings, 80000);
        await w.Goals.CreateAsync(new GoalInput("Emergency", 100000, D(12, 1), GoalTracking.AccountBalance, AccountId: savings.Id));
        var trip = new Scenario("Trip", [new ScenarioChange(ScenarioChangeKind.Purchase, D(11, 1), 30000, savings.Id, Name: "Trip")]);

        var impact = Assert.Single((await w.Scenarios.CompareAsync(Today, 90, trip)).Goals);
        Assert.Equal(("Emergency", 80000L, 50000L), (impact.GoalName, impact.BaselineCents, impact.ScenarioCents));

        await w.Scenarios.SaveAsync(trip);
        await w.Scenarios.SaveAsync(trip with { Name = " trip " }); // same name: replaced, not duplicated
        var saved = Assert.Single(await w.Scenarios.GetSavedAsync());
        Assert.Equal(("trip", ScenarioChangeKind.Purchase, 30000L, D(11, 1)), (saved.Name, saved.Changes[0].Kind, saved.Changes[0].AmountCents, saved.Changes[0].From));
        await w.Scenarios.DeleteAsync("TRIP");
        Assert.Empty(await w.Scenarios.GetSavedAsync());
    }
}

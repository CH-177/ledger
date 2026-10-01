using Ledger.Core.Models;
using Ledger.Core.Services;

namespace Ledger.Core.Tests;

public class ForecastServiceTests
{
    private static readonly DateOnly Today = new(2026, 9, 26); // Saturday
    private static DateOnly D(int m, int d) => new(2026, m, d);

    private sealed class Fixture : IAsyncDisposable
    {
        public required TestDatabase Db { get; init; }
        public required RecurringService Recurring { get; init; }
        public required TransactionService Transactions { get; init; }
        public required IncomeService Income { get; init; }
        public required ForecastService Forecast { get; init; }
        public required Account Checking { get; init; }
        public required Account Savings { get; init; }
        public required Account Card { get; init; }
        public ValueTask DisposeAsync() => Db.DisposeAsync();

        public Task<RecurringTransaction> RuleAsync(string name, TransactionType type, long cents, DateOnly start, int accountId,
            int? to = null, bool estimated = false, RecurrenceFrequency frequency = RecurrenceFrequency.Monthly) =>
            Recurring.CreateAsync(new RecurringInput(name, type, cents, frequency, start, accountId, null, to, null, estimated));
    }

    private static async Task<Fixture> CreateAsync()
    {
        var db = await TestDatabase.CreateAsync(Today);
        var accounts = new AccountService(db.Factory, db.Time);
        var recurring = new RecurringService(db.Factory, db.Time);
        var income = new IncomeService(db.Factory, new SettingsService(db.Factory));
        return new Fixture
        {
            Db = db,
            Recurring = recurring,
            Transactions = new TransactionService(db.Factory, db.Time),
            Income = income,
            Forecast = new ForecastService(db.Factory, accounts, recurring, new IncomeForecastService(db.Factory, income)),
            Checking = await db.AddAccountAsync("Checking", AccountType.Checking, 42000),
            Savings = await db.AddAccountAsync("Savings", AccountType.Savings, 56621),
            Card = await db.AddAccountAsync("Card", AccountType.CreditCard, -15000),
        };
    }

    [Fact]
    public async Task Projects_the_balance_event_by_event_from_the_actual_balance()
    {
        await using var f = await CreateAsync();
        await f.RuleAsync("Car insurance", TransactionType.Expense, 20000, D(9, 30), f.Checking.Id);
        await f.RuleAsync("MacBook", TransactionType.Expense, 3790, D(10, 15), f.Checking.Id);
        await f.RuleAsync("Photography", TransactionType.Income, 10000, D(10, 3), f.Checking.Id, estimated: true);

        var forecast = await f.Forecast.GetForecastAsync(Today, 30);

        Assert.Equal(98621, forecast.StartingBalanceCents); // checking + savings; the card is owed, not had
        Assert.Equal(["Checking", "Savings"], forecast.Accounts.Select(a => a.Name));
        Assert.Equal(
            [(D(9, 30), "Car insurance", -20000L, 78621L), (D(10, 3), "Photography", 10000L, 88621L), (D(10, 15), "MacBook", -3790L, 84831L)],
            forecast.Events.Select(e => (e.Date, e.Name, e.SignedCents, e.BalanceAfterCents)));
        Assert.True(forecast.Events[1].IsEstimated);
        Assert.Equal((78621L, D(9, 30)), forecast.Lowest);
        Assert.Equal((10000L, 23790L, 84831L), (forecast.ExpectedInCents, forecast.ExpectedOutCents, forecast.EndingBalanceCents));
    }

    [Fact]
    public async Task Transfers_count_only_when_money_leaves_or_enters_the_covered_accounts()
    {
        await using var f = await CreateAsync();
        await f.RuleAsync("Card payment", TransactionType.Transfer, 5000, D(10, 5), f.Checking.Id, to: f.Card.Id);
        await f.RuleAsync("To savings", TransactionType.Transfer, 2500, D(10, 1), f.Checking.Id, to: f.Savings.Id);
        await f.RuleAsync("Spotify", TransactionType.Expense, 1199, D(10, 10), f.Card.Id); // on the card: not cash yet

        var combined = await f.Forecast.GetForecastAsync(Today, 30);
        Assert.Equal([("Card payment", -5000L)], combined.Events.Select(e => (e.Name, e.SignedCents)));

        var checking = await f.Forecast.GetForecastAsync(Today, 30, f.Checking.Id);
        Assert.Equal(42000, checking.StartingBalanceCents);
        Assert.Equal([("To savings", -2500L), ("Card payment", -5000L)], checking.Events.Select(e => (e.Name, e.SignedCents)));

        var savings = await f.Forecast.GetForecastAsync(Today, 30, f.Savings.Id);
        Assert.Equal([("To savings", 2500L)], savings.Events.Select(e => (e.Name, e.SignedCents)));

        var card = await f.Forecast.GetForecastAsync(Today, 30, f.Card.Id);
        Assert.Equal([("Card payment", 5000L), ("Spotify", -1199L)], card.Events.Select(e => (e.Name, e.SignedCents)));
    }

    [Fact]
    public async Task Recorded_and_skipped_occurrences_leave_the_forecast_and_missed_ones_land_on_today()
    {
        await using var f = await CreateAsync();
        f.Db.Time.Today = D(9, 1);
        var phone = await f.RuleAsync("Phone", TransactionType.Expense, 4000, D(9, 20), f.Checking.Id);
        var gym = await f.RuleAsync("Gym", TransactionType.Expense, 2500, D(9, 10), f.Checking.Id);
        var rent = await f.RuleAsync("Rent", TransactionType.Expense, 30000, D(10, 1), f.Checking.Id);
        f.Db.Time.Today = Today;

        // Gym was paid and recorded; Phone was missed; October rent is skipped.
        var paid = await f.Transactions.CreateAsync(new TransactionInput(TransactionType.Expense, 2500, D(9, 10), f.Checking.Id));
        await f.Recurring.RecordAsync(gym.Id, D(9, 10), paid.Id);
        await f.Recurring.SkipAsync(rent.Id, D(10, 1));

        var forecast = await f.Forecast.GetForecastAsync(Today, 12); // through Oct 8: before Gym's October date
        var missed = Assert.Single(forecast.Events);
        Assert.Equal((Today, "Phone", -4000L, true, D(9, 20)), (missed.Date, missed.Name, missed.SignedCents, missed.IsOverdue, missed.ScheduledDate));
        Assert.Equal(phone.Id, missed.RecurringId);
        Assert.Equal(98621 - 2500, forecast.StartingBalanceCents); // the recorded gym payment is real money
    }

    [Fact]
    public async Task Future_dated_transactions_are_included_but_not_counted_twice()
    {
        await using var f = await CreateAsync();
        await f.Transactions.CreateAsync(new TransactionInput(TransactionType.Expense, 2000, D(10, 3), f.Checking.Id, Payee: "Dentist"));
        await f.Transactions.CreateAsync(new TransactionInput(TransactionType.Expense, 1000, Today, f.Checking.Id, Payee: "Quickmart"));

        var forecast = await f.Forecast.GetForecastAsync(Today, 30);
        Assert.Equal(98621 - 1000, forecast.StartingBalanceCents); // today's spending is already in the balance
        var dentist = Assert.Single(forecast.Events);
        Assert.Equal((ForecastEventKind.Scheduled, -2000L, false), (dentist.Kind, dentist.SignedCents, dentist.IsEstimated));
    }

    [Fact]
    public async Task Money_out_comes_before_money_in_on_the_same_day()
    {
        await using var f = await CreateAsync();
        await f.RuleAsync("Paycheck", TransactionType.Income, 50000, D(10, 2), f.Checking.Id);
        await f.RuleAsync("Rent", TransactionType.Expense, 120000, D(10, 2), f.Checking.Id);

        var forecast = await f.Forecast.GetForecastAsync(Today, 10);
        Assert.Equal(["Rent", "Paycheck"], forecast.Events.Select(e => e.Name));
        Assert.Equal((98621L - 120000, D(10, 2)), forecast.Lowest); // the dip below zero is shown, not hidden
    }

    [Fact]
    public async Task Predicted_paychecks_are_expected_income_until_recorded()
    {
        await using var f = await CreateAsync();
        var job = await f.Income.CreateJobAsync(JobInput(depositAccountId: f.Checking.Id));
        await f.Income.AddShiftAsync(new ShiftInput(job.Id, D(9, 29), 240, null, null, null, ShiftStatus.Scheduled, null));
        await f.Income.AddShiftAsync(new ShiftInput(job.Id, D(10, 6), 240, null, null, null, ShiftStatus.Scheduled, null));

        var forecast = await f.Forecast.GetForecastAsync(Today, 30);
        var paychecks = forecast.Events.Where(e => e.Kind == ForecastEventKind.Paycheck).ToList();
        Assert.Equal([D(10, 9), D(10, 16)], paychecks.Select(p => p.Date)); // Friday after each Sun–Sat week
        Assert.All(paychecks, p => Assert.True(p.IsEstimated && p.SignedCents > 0 && p.Name == "Riverside Market paycheck"));

        // Only the job's deposit account sees it on a single-account forecast.
        Assert.Equal(2, (await f.Forecast.GetForecastAsync(Today, 30, f.Checking.Id)).Events.Count);
        Assert.Empty((await f.Forecast.GetForecastAsync(Today, 30, f.Savings.Id)).Events);

        // Once the real paycheck is recorded, the estimate leaves the forecast.
        var forecastPaychecks = new IncomeForecastService(f.Db.Factory, f.Income);
        var first = (await forecastPaychecks.GetPaychecksAsync(D(10, 9), D(10, 9)))[0];
        var real = await f.Transactions.CreateAsync(new TransactionInput(TransactionType.Income, 4700, D(10, 9), f.Checking.Id));
        await forecastPaychecks.RecordPaycheckAsync(job.Id, first.Period, first.NetCents, real.Id);
        // (Recorded ahead of payday, so the real amount itself is a scheduled event in its place.)
        Assert.Equal([(D(10, 9), ForecastEventKind.Scheduled, 4700L), (D(10, 16), ForecastEventKind.Paycheck, paychecks[1].SignedCents)],
            (await f.Forecast.GetForecastAsync(Today, 30)).Events.Select(e => (e.Date, e.Kind, e.SignedCents)));
    }

    [Fact]
    public async Task An_empty_forecast_is_just_todays_balance()
    {
        await using var f = await CreateAsync();
        var forecast = await f.Forecast.GetForecastAsync(Today, 30);
        Assert.Empty(forecast.Events);
        Assert.Equal((98621L, 98621L, (98621L, Today)), (forecast.StartingBalanceCents, forecast.EndingBalanceCents, forecast.Lowest));
    }

    private static JobInput JobInput(int? depositAccountId) => new(
        Name: "Riverside Market",
        HourlyRateCents: 1350,
        RateEffectiveDate: new DateOnly(2026, 9, 1),
        PayFrequency: PayFrequency.Weekly,
        PayPeriodAnchor: new DateOnly(2026, 9, 20),
        PaydayOffsetDays: 6,
        IsPaydayConfirmed: false,
        WorkweekStart: DayOfWeek.Sunday,
        IsOvertimeEligible: true,
        DepositAccountId: depositAccountId,
        IncomeCategoryId: null,
        FilingStatus: FilingStatus.Single,
        W4MultipleJobs: false,
        W4DependentsCreditCents: 0,
        W4OtherIncomeCents: 0,
        W4DeductionsCents: 0,
        W4ExtraWithholdingCents: 0,
        WorkLocationName: "Riverton City",
        WorkPsdCode: "990201",
        WorkNonResidentEitBasisPoints: 50,
        WorkLstAnnualCents: 5200,
        IsLstExempt: false);
}

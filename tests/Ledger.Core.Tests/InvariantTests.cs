using System.Text;
using Ledger.Core.Forecasting;
using Ledger.Core.Models;
using Ledger.Core.Payroll;
using Ledger.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Core.Tests;

/// <summary>
/// Properties that must hold for any data, not just the worked examples: reading never writes,
/// money is conserved, every page reports the same numbers, and deleted means gone everywhere.
/// Randomized cases use fixed seeds, so a failure always reproduces.
/// </summary>
public class InvariantTests
{
    private static readonly DateOnly Today = new(2026, 9, 27);

    /// <summary>A realistic ledger: accounts of every kind, a month of mixed history, bills, a job with shifts, goals, a budget.</summary>
    private sealed class World : IAsyncDisposable
    {
        public required TestDatabase Db { get; init; }
        public required AccountService Accounts { get; init; }
        public required TransactionService Transactions { get; init; }
        public required RecurringService Recurring { get; init; }
        public required IncomeService Income { get; init; }
        public required IncomeForecastService Paychecks { get; init; }
        public required ForecastService Forecasts { get; init; }
        public required BudgetService Budgets { get; init; }
        public required GoalService Goals { get; init; }
        public required DashboardService Dashboard { get; init; }
        public required ReportService Reports { get; init; }
        public required ExportService Export { get; init; }
        public required Account Checking { get; init; }
        public required Account Savings { get; init; }
        public required Account Card { get; init; }
        public required int Groceries { get; init; }
        public ValueTask DisposeAsync() => Db.DisposeAsync();

        public static async Task<World> CreateAsync()
        {
            var db = await TestDatabase.CreateAsync(Today);
            var accounts = new AccountService(db.Factory, db.Time);
            var tx = new TransactionService(db.Factory, db.Time);
            var recurring = new RecurringService(db.Factory, db.Time);
            var income = new IncomeService(db.Factory, new SettingsService(db.Factory));
            var paychecks = new IncomeForecastService(db.Factory, income);
            int groceries;
            await using (var ctx = db.Context())
            {
                groceries = await ctx.Categories.Where(c => c.Name == "Groceries").Select(c => c.Id).FirstAsync();
            }
            return new World
            {
                Db = db,
                Accounts = accounts,
                Transactions = tx,
                Recurring = recurring,
                Income = income,
                Paychecks = paychecks,
                Forecasts = new ForecastService(db.Factory, accounts, recurring, paychecks),
                Budgets = new BudgetService(db.Factory),
                Goals = new GoalService(db.Factory, accounts, db.Time),
                Dashboard = new DashboardService(db.Factory, tx),
                Reports = new ReportService(tx, accounts),
                Export = new ExportService(db.Factory, db.Time),
                Checking = await db.AddAccountAsync("Checking", AccountType.Checking, 50000),
                Savings = await db.AddAccountAsync("Savings", AccountType.Savings, 56621),
                Card = await db.AddAccountAsync("Card", AccountType.CreditCard, -2500),
                Groceries = groceries,
            };
        }

        public async Task PopulateAsync()
        {
            await Transactions.CreateAsync(new TransactionInput(TransactionType.Income, 16312, new DateOnly(2026, 9, 11), Checking.Id, Payee: "Riverside Market"));
            await Transactions.CreateAsync(new TransactionInput(TransactionType.Expense, 842, new DateOnly(2026, 9, 12), Checking.Id, CategoryId: Groceries));
            await Transactions.CreateAsync(new TransactionInput(TransactionType.Expense, 5000, new DateOnly(2026, 9, 13), Card.Id, CategoryId: Groceries));
            await Transactions.CreateAsync(new TransactionInput(TransactionType.Refund, 1500, new DateOnly(2026, 9, 15), Card.Id, CategoryId: Groceries));
            await Transactions.CreateAsync(new TransactionInput(TransactionType.Transfer, 5000, new DateOnly(2026, 9, 20), Checking.Id, Card.Id));
            await Transactions.CreateAsync(new TransactionInput(TransactionType.Transfer, 2000, new DateOnly(2026, 9, 21), Checking.Id, Savings.Id));
            await Transactions.CreateAsync(new TransactionInput(TransactionType.Expense, 1199, new DateOnly(2026, 10, 3), Checking.Id)); // future-dated
            var deleted = await Transactions.CreateAsync(new TransactionInput(TransactionType.Expense, 99999, new DateOnly(2026, 9, 14), Checking.Id, CategoryId: Groceries));
            await Transactions.DeleteAsync(deleted.Id);

            var rule = await Recurring.CreateAsync(new RecurringInput("Insurance", TransactionType.Expense, 20000, RecurrenceFrequency.Monthly, new DateOnly(2026, 9, 30), Checking.Id));
            await Recurring.SkipAsync(rule.Id, new DateOnly(2026, 10, 30));

            var job = await Income.CreateJobAsync(new JobInput("Riverside Market", 1350, new DateOnly(2026, 9, 1), PayFrequency.Weekly, new DateOnly(2026, 9, 20), 6,
                false, DayOfWeek.Sunday, true, Checking.Id, null, FilingStatus.Single, false, 0, 0, 0, 0, "Riverton City", "990201", 50, 5200, false));
            await Income.AddShiftAsync(new ShiftInput(job.Id, new DateOnly(2026, 9, 29), 360, null, null, null, ShiftStatus.Scheduled, null));

            await Budgets.SetPlannedAsync(2026, 9, await ParentOfAsync(Groceries), 25000);
            var goal = await Goals.CreateAsync(new GoalInput("MacBook", 120000, new DateOnly(2027, 6, 15), GoalTracking.Manual, 82000));
            await Goals.AddContributionAsync(goal.Id, 1000, Today);
            await Goals.CreateAsync(new GoalInput("Emergency", 100000, null, GoalTracking.AccountBalance, 0, Savings.Id));
        }

        public async Task<int> ParentOfAsync(int categoryId)
        {
            await using var ctx = Db.Context();
            return await ctx.Categories.Where(c => c.Id == categoryId).Select(c => c.ParentCategoryId ?? c.Id).FirstAsync();
        }
    }

    /// <summary>Every row of every table, as text: any write anywhere changes it.</summary>
    private static async Task<string> SnapshotAsync(TestDatabase db)
    {
        await using var ctx = db.Context();
        var connection = ctx.Database.GetDbConnection();
        await connection.OpenAsync();
        var tables = new List<string>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;";
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) tables.Add(r.GetString(0));
        }
        var sb = new StringBuilder();
        foreach (var table in tables)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT * FROM \"{table}\" ORDER BY rowid;";
            await using var r = await cmd.ExecuteReaderAsync();
            sb.Append('#').AppendLine(table);
            while (await r.ReadAsync())
            {
                for (var i = 0; i < r.FieldCount; i++) sb.Append(r.GetValue(i)).Append('|');
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    [Fact]
    public async Task Forecasts_reports_and_every_other_read_never_change_stored_data()
    {
        await using var w = await World.CreateAsync();
        await w.PopulateAsync();
        var before = await SnapshotAsync(w.Db);

        await w.Forecasts.GetForecastAsync(Today, 90);
        await w.Forecasts.GetForecastAsync(Today, 90, w.Checking.Id);
        await w.Paychecks.GetPaychecksAsync(Today.AddDays(-366), Today.AddDays(90));
        await w.Paychecks.GetWindowsAsync(Today, byWorkDate: true);
        await w.Recurring.GetOccurrencesAsync(Today.AddDays(-366), Today.AddYears(2));
        await w.Recurring.GetRulesAsync();
        await w.Dashboard.GetMonthSummaryAsync(Today);
        await w.Reports.GetCashFlowAsync(Today, 12);
        await w.Reports.GetNetWorthHistoryAsync(Today, 12);
        await w.Budgets.GetMonthAsync(2026, 9);
        await w.Goals.GetGoalsAsync(includeArchived: true);
        await w.Accounts.GetAccountBalancesAsync(includeArchived: true);
        await w.Accounts.GetNetWorthAsync();
        await w.Transactions.QueryAsync(new TransactionQuery());
        await w.Export.WriteTransactionsCsvAsync(Stream.Null);
        await w.Export.WriteJsonAsync(Stream.Null, "test");
        // What-ifs are reads too, however much they change.
        var scenarios = new ScenarioService(w.Forecasts, w.Goals, new SettingsService(w.Db.Factory));
        await scenarios.CompareAsync(Today, 180, new Ledger.Core.Forecasting.Scenario("What if", [
            new(Ledger.Core.Forecasting.ScenarioChangeKind.Purchase, Today.AddDays(5), 99999, w.Checking.Id),
            new(Ledger.Core.Forecasting.ScenarioChangeKind.WeeklyHours, Today, WeeklyMinutes: 45 * 60),
            new(Ledger.Core.Forecasting.ScenarioChangeKind.PayRate, Today, 2000),
            new(Ledger.Core.Forecasting.ScenarioChangeKind.StopRecurring, Today, RecurringId: (await w.Recurring.GetRulesAsync())[0].Id),
            new(Ledger.Core.Forecasting.ScenarioChangeKind.AddIncome, Today, 5000, w.Checking.Id, Frequency: RecurrenceFrequency.Weekly),
        ]), cushionCents: 10000);
        await scenarios.CanIAffordAsync(Today, 120000, Today.AddDays(30), w.Checking.Id, 5000);
        await scenarios.GetSavedAsync();
        await w.Goals.GetFundsAsync();
        await w.Goals.GetSetAsideInMonthAsync(2026, 9);

        Assert.Equal(before, await SnapshotAsync(w.Db));

        // The snapshot really does see writes (so the check above can fail).
        await w.Recurring.UnskipAsync((await w.Recurring.GetRulesAsync())[0].Id, new DateOnly(2026, 10, 30));
        Assert.NotEqual(before, await SnapshotAsync(w.Db));
    }

    [Fact]
    public async Task Every_page_reports_the_same_spending_and_income_for_the_month()
    {
        await using var w = await World.CreateAsync();
        await w.PopulateAsync();

        var dashboard = await w.Dashboard.GetMonthSummaryAsync(Today);
        var budget = await w.Budgets.GetMonthAsync(2026, 9);
        var report = (await w.Reports.GetCashFlowAsync(Today, 1)).Single();
        var list = await w.Transactions.GetTotalsAsync(new TransactionQuery(From: new DateOnly(2026, 9, 1), To: Today));

        // $8.42 + $50.00 card purchase − $15.00 refund. Not the card payment, not the transfer to
        // savings, not the deleted $999.99, not October's future-dated $11.99. The refund is not income.
        Assert.Equal(4342, dashboard.ExpenseCents);
        Assert.Equal(dashboard.ExpenseCents, budget.ActualCents);
        Assert.Equal(dashboard.ExpenseCents, report.ExpenseCents);
        Assert.Equal(dashboard.ExpenseCents, list.ExpenseCents);
        Assert.Equal(16312, dashboard.IncomeCents);
        Assert.Equal(dashboard.IncomeCents, report.IncomeCents);
        Assert.Equal(dashboard.IncomeCents, list.IncomeCents);
    }

    [Fact]
    public async Task Todays_balance_is_the_same_everywhere_it_appears()
    {
        await using var w = await World.CreateAsync();
        await w.PopulateAsync();

        var balances = await w.Accounts.GetAccountBalancesAsync();
        var spendable = balances.Where(b => !b.Account.IsLiability).Sum(b => b.BalanceCents);
        var forecast = await w.Forecasts.GetForecastAsync(Today, 30);
        var worth = await w.Accounts.GetNetWorthAsync();
        var history = await w.Reports.GetNetWorthHistoryAsync(Today, 1);

        Assert.Equal(spendable, forecast.StartingBalanceCents);
        Assert.Equal(worth.NetWorthCents, history.Single().NetWorthCents);
        Assert.Equal(balances.First(b => b.Account.Id == w.Savings.Id).BalanceCents,
            (await w.Goals.GetGoalsAsync()).Single(g => g.Goal.Name == "Emergency").CurrentCents);
    }

    [Fact]
    public async Task A_deleted_transaction_counts_nowhere_and_restoring_it_counts_everywhere()
    {
        await using var w = await World.CreateAsync();
        var tx = await w.Transactions.CreateAsync(new TransactionInput(TransactionType.Expense, 4321, new DateOnly(2026, 9, 12), w.Checking.Id, CategoryId: w.Groceries));
        await w.Budgets.SetPlannedAsync(2026, 9, await w.ParentOfAsync(w.Groceries), 10000);

        async Task<long[]> Everywhere() =>
        [
            (await w.Dashboard.GetMonthSummaryAsync(Today)).ExpenseCents,
            (await w.Budgets.GetMonthAsync(2026, 9)).ActualCents,
            (await w.Reports.GetCashFlowAsync(Today, 1)).Single().ExpenseCents,
            (await w.Transactions.GetTotalsAsync(new TransactionQuery())).ExpenseCents,
            50000 - await w.Accounts.GetBalanceAsync(w.Checking.Id),
            50000 - (await w.Forecasts.GetForecastAsync(Today, 1, w.Checking.Id)).StartingBalanceCents,
        ];

        Assert.All(await Everywhere(), v => Assert.Equal(4321, v));
        await w.Transactions.DeleteAsync(tx.Id);
        Assert.All(await Everywhere(), v => Assert.Equal(0, v));
        using (var csv = new MemoryStream())
        {
            await w.Export.WriteTransactionsCsvAsync(csv);
            Assert.DoesNotContain("43.21", Encoding.UTF8.GetString(csv.ToArray()));
        }
        await w.Transactions.RestoreAsync(tx.Id);
        Assert.All(await Everywhere(), v => Assert.Equal(4321, v));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Money_is_conserved_and_transfers_never_change_net_worth(int seed)
    {
        await using var w = await World.CreateAsync();
        var random = new Random(seed);
        Account[] all = [w.Checking, w.Savings, w.Card];
        long income = 0, expense = 0, refunds = 0, outside = 0;

        for (var i = 0; i < 300; i++)
        {
            var date = new DateOnly(2026, 1, 1).AddDays(random.Next(270)); // all on or before today
            var amount = random.NextInt64(1, 500000);
            var from = all[random.Next(all.Length)];
            switch (random.Next(6))
            {
                case 4:
                    await w.Transactions.CreateAsync(new TransactionInput(TransactionType.OutsideIn, amount, date, from.Id));
                    outside += amount;
                    break;
                case 5:
                    await w.Transactions.CreateAsync(new TransactionInput(TransactionType.OutsideOut, amount, date, from.Id));
                    outside -= amount;
                    break;
                case 3:
                    await w.Transactions.CreateAsync(new TransactionInput(TransactionType.Refund, amount, date, from.Id));
                    refunds += amount;
                    break;
                case 0:
                    await w.Transactions.CreateAsync(new TransactionInput(TransactionType.Income, amount, date, from.Id));
                    income += amount;
                    break;
                case 1:
                    await w.Transactions.CreateAsync(new TransactionInput(TransactionType.Expense, amount, date, from.Id));
                    expense += amount;
                    break;
                default:
                    var worthBefore = (await w.Accounts.GetNetWorthAsync()).NetWorthCents;
                    var to = all.First(a => a.Id != from.Id);
                    await w.Transactions.CreateAsync(new TransactionInput(TransactionType.Transfer, amount, date, from.Id, to.Id));
                    Assert.Equal(worthBefore, (await w.Accounts.GetNetWorthAsync()).NetWorthCents);
                    break;
            }
        }

        // Every account's balance is its start plus its own movements; together they sum to
        // the starting total plus income minus spending, whatever moved between them.
        var starting = 50000L + 56621 - 2500;
        var balances = await w.Accounts.GetAccountBalancesAsync();
        Assert.Equal(starting + income - expense + refunds + outside, balances.Sum(b => b.BalanceCents));
        Assert.Equal(starting + income - expense + refunds + outside, (await w.Accounts.GetNetWorthAsync()).NetWorthCents);
        var totals = await w.Transactions.GetTotalsAsync(new TransactionQuery());
        Assert.Equal((income, expense - refunds, refunds), (totals.IncomeCents, totals.ExpenseCents, totals.RefundCents));
    }

    [Fact]
    public void Goal_math_never_produces_invalid_values()
    {
        var random = new Random(12);
        for (var i = 0; i < 20000; i++)
        {
            var target = random.NextInt64(1, Money.MaxAmountCents + 1);
            var current = random.Next(4) switch
            {
                0 => 0,
                1 => target,
                2 => random.NextInt64(0, Money.MaxAmountCents * 50), // many contributions
                _ => random.NextInt64(0, target),
            };
            var today = new DateOnly(2026, 1, 1).AddDays(random.Next(3000));
            DateOnly? date = random.Next(5) == 0 ? null : today.AddDays(random.Next(-400, 4000));

            var percent = GoalMath.Percent(target, current);
            var fraction = GoalMath.Fraction(target, current);
            Assert.InRange(percent, 0, 100);
            Assert.InRange(fraction, 0.0, 1.0);
            Assert.False(double.IsNaN(fraction));
            Assert.True(percent < 100 || current >= target, "100% only when reached");
            Assert.InRange(GoalMath.Remaining(target, current), 0, target);

            if (GoalMath.SuggestedMonthly(target, current, today, date) is { } monthly)
            {
                var months = GoalMath.MonthsLeft(today, date!.Value);
                Assert.True(monthly > 0);
                Assert.True(monthly * months >= GoalMath.Remaining(target, current), "following the suggestion is never short");
                Assert.True((monthly - 1) * months < GoalMath.Remaining(target, current), "and never more than a cent a month over");
            }
        }
    }

    [Fact]
    public async Task The_schema_has_the_indexes_and_constraints_the_brief_requires()
    {
        await using var db = await TestDatabase.CreateAsync(Today);
        await using var ctx = db.Context();
        var connection = ctx.Database.GetDbConnection();
        await connection.OpenAsync();

        async Task<List<string>> ListAsync(string sql)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            await using var r = await cmd.ExecuteReaderAsync();
            var list = new List<string>();
            while (await r.ReadAsync()) list.Add(r.GetString(0));
            return list;
        }

        var indexes = await ListAsync("SELECT tbl_name || '.' || COALESCE((SELECT group_concat(name) FROM pragma_index_info(m.name)), '') FROM sqlite_master m WHERE type = 'index';");
        foreach (var required in new[] { "Transactions.AccountId", "Transactions.CategoryId", "Transactions.Date", "Transactions.TransactionType",
                     "BudgetItems.BudgetId", "RecurringTransactions.StartDate", "Transactions.RecurringTransactionId,RecurrenceDate" })
        {
            Assert.Contains(required, indexes);
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA foreign_keys;";
            Assert.Equal(1L, await cmd.ExecuteScalarAsync()); // enforced on every connection the app opens
        }
        var tables = await ListAsync("SELECT sql FROM sqlite_master WHERE type = 'table' AND name IN ('Transactions', 'RecurringTransactions', 'Goals', 'Shifts');");
        Assert.All(tables, sql => Assert.Contains("CHECK", sql));
    }
}

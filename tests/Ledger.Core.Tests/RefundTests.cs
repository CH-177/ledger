using Ledger.Core.Models;
using Ledger.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Core.Tests;

public class RefundTests
{
    private static readonly DateOnly Today = new(2026, 10, 20);

    private sealed class Fixture : IAsyncDisposable
    {
        public required TestDatabase Db { get; init; }
        public required TransactionService Transactions { get; init; }
        public required BudgetService Budgets { get; init; }
        public required AccountService Accounts { get; init; }
        public required ReportService Reports { get; init; }
        public required Account Checking { get; init; }
        public required Account Card { get; init; }
        public required int Groceries { get; init; }
        public required int Food { get; init; }
        public required int Paycheck { get; init; }
        public ValueTask DisposeAsync() => Db.DisposeAsync();

        public Task<Transaction> AddAsync(TransactionType type, long cents, DateOnly date, Account? account = null, int? category = null) =>
            Transactions.CreateAsync(new TransactionInput(type, cents, date, (account ?? Checking).Id, CategoryId: category ?? Groceries, Payee: "Target"));
    }

    private static async Task<Fixture> CreateAsync()
    {
        var db = await TestDatabase.CreateAsync(Today);
        var accounts = new AccountService(db.Factory, db.Time);
        var tx = new TransactionService(db.Factory, db.Time);
        await using var ctx = db.Context();
        var groceries = await ctx.Categories.FirstAsync(c => c.Name == "Groceries");
        return new Fixture
        {
            Db = db,
            Transactions = tx,
            Budgets = new BudgetService(db.Factory),
            Accounts = accounts,
            Reports = new ReportService(tx, accounts),
            Checking = await db.AddAccountAsync("Checking", AccountType.Checking, 50000),
            Card = await db.AddAccountAsync("Card", AccountType.CreditCard),
            Groceries = groceries.Id,
            Food = groceries.ParentCategoryId ?? groceries.Id,
            Paycheck = (await ctx.Categories.FirstAsync(c => c.CategoryType == CategoryType.Income)).Id,
        };
    }

    [Fact]
    public async Task A_refund_in_the_same_month_lowers_spending_and_is_never_income()
    {
        await using var f = await CreateAsync();
        await f.AddAsync(TransactionType.Expense, 6000, new(2026, 10, 3));
        await f.AddAsync(TransactionType.Refund, 2500, new(2026, 10, 9));

        var totals = await f.Transactions.GetTotalsAsync(new TransactionQuery(From: new(2026, 10, 1), To: Today));
        Assert.Equal((0L, 3500L, 2500L), (totals.IncomeCents, totals.ExpenseCents, totals.RefundCents));
        Assert.Equal(3500, (await f.Budgets.GetMonthAsync(2026, 10)).Lines.Single(l => l.CategoryId == f.Food).ActualCents);
        Assert.Equal(50000 - 6000 + 2500, await f.Accounts.GetBalanceAsync(f.Checking.Id));
        var flow = (await f.Reports.GetCashFlowAsync(Today, 1)).Single();
        Assert.Equal((0L, 3500L), (flow.IncomeCents, flow.ExpenseCents));
    }

    [Fact]
    public async Task A_refund_in_a_later_month_lowers_that_months_spending_even_below_zero()
    {
        await using var f = await CreateAsync();
        await f.AddAsync(TransactionType.Expense, 6000, new(2026, 9, 28));
        await f.AddAsync(TransactionType.Refund, 6000, new(2026, 10, 2));

        // Cash basis: September keeps its spending; October, with nothing else spent, goes negative.
        Assert.Equal(6000, (await f.Budgets.GetMonthAsync(2026, 9)).ActualCents);
        var october = await f.Budgets.GetMonthAsync(2026, 10);
        Assert.Equal(-6000, october.Lines.Single(l => l.CategoryId == f.Food).ActualCents);
        Assert.Equal(-6000, (await f.Transactions.GetTotalsAsync(new TransactionQuery(From: new(2026, 10, 1), To: Today))).ExpenseCents);
    }

    [Fact]
    public async Task A_refund_on_a_card_lowers_what_is_owed()
    {
        await using var f = await CreateAsync();
        await f.AddAsync(TransactionType.Expense, 4000, new(2026, 10, 5), f.Card);
        await f.AddAsync(TransactionType.Refund, 1500, new(2026, 10, 6), f.Card);
        Assert.Equal(-2500, await f.Accounts.GetBalanceAsync(f.Card.Id));
    }

    [Fact]
    public async Task A_refund_needs_a_spending_category_and_is_never_scheduled()
    {
        await using var f = await CreateAsync();
        var ex = await Assert.ThrowsAsync<LedgerValidationException>(() => f.AddAsync(TransactionType.Refund, 100, new(2026, 10, 1), category: f.Paycheck));
        Assert.Contains("spending category", ex.Message);

        var recurring = new RecurringService(f.Db.Factory, f.Db.Time);
        await Assert.ThrowsAsync<LedgerValidationException>(() => recurring.CreateAsync(
            new RecurringInput("Rebate", TransactionType.Refund, 100, RecurrenceFrequency.Monthly, new(2026, 10, 1), f.Checking.Id)));
    }

    [Fact]
    public async Task Payee_suggestions_for_a_refund_come_from_spending()
    {
        await using var f = await CreateAsync();
        await f.AddAsync(TransactionType.Expense, 6000, new(2026, 10, 3));
        var suggestion = Assert.Single(await f.Transactions.GetPayeeSuggestionsAsync("tar", TransactionType.Refund));
        Assert.Equal(("Target", (int?)f.Groceries), (suggestion.Payee, suggestion.CategoryId));
    }
}

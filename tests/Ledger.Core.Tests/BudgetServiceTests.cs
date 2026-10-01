using Ledger.Core.Models;
using Ledger.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Core.Tests;

public class BudgetServiceTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        public required TestDatabase Db { get; init; }
        public required BudgetService Budgets { get; init; }
        public required TransactionService Transactions { get; init; }
        public required Account Checking { get; init; }
        public required Account Savings { get; init; }
        public required Account Card { get; init; }
        public required Dictionary<string, int> Cat { get; init; }

        public Task SpendAsync(long cents, string category, DateOnly date, Account? account = null) =>
            Transactions.CreateAsync(new TransactionInput(TransactionType.Expense, cents, date, (account ?? Checking).Id, CategoryId: Cat[category]));

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private static async Task<Fixture> CreateAsync()
    {
        var db = await TestDatabase.CreateAsync(new DateOnly(2026, 9, 26));
        var checking = await db.AddAccountAsync("Checking", AccountType.Checking, 500_000);
        var savings = await db.AddAccountAsync("Savings", AccountType.Savings);
        var card = await db.AddAccountAsync("Visa", AccountType.CreditCard);
        await using var ctx = db.Context();
        return new Fixture
        {
            Db = db,
            Budgets = new BudgetService(db.Factory),
            Transactions = new TransactionService(db.Factory, db.Time),
            Checking = checking, Savings = savings, Card = card,
            Cat = (await ctx.Categories.ToListAsync()).GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.First().Id),
        };
    }

    private static BudgetLine Line(BudgetMonth m, string name) => m.Lines.Single(l => l.Name == name);

    [Fact]
    public async Task Planned_250_actual_187_leaves_63_remaining()
    {
        await using var f = await CreateAsync();
        await f.Budgets.SetPlannedAsync(2026, 9, f.Cat["Food"], 25000);
        await f.SpendAsync(18700, "Groceries", new(2026, 9, 10));

        var food = Line(await f.Budgets.GetMonthAsync(2026, 9), "Food");
        Assert.Equal(25000, food.PlannedCents);
        Assert.Equal(18700, food.ActualCents);
        Assert.Equal(6300, food.RemainingCents);
        Assert.Equal(BudgetLineStatus.OnTrack, food.Status);
    }

    [Fact]
    public async Task Subcategory_spending_rolls_up_into_its_group()
    {
        await using var f = await CreateAsync();
        await f.SpendAsync(1000, "Groceries", new(2026, 9, 1));
        await f.SpendAsync(2000, "Restaurants", new(2026, 9, 2));
        await f.SpendAsync(300, "Food", new(2026, 9, 3));

        Assert.Equal(3300, Line(await f.Budgets.GetMonthAsync(2026, 9), "Food").ActualCents);
    }

    [Fact]
    public async Task Income_and_transfers_never_count_as_spending()
    {
        await using var f = await CreateAsync();
        await f.Transactions.CreateAsync(new TransactionInput(TransactionType.Income, 61240, new(2026, 9, 5), f.Checking.Id, CategoryId: f.Cat["Paycheck"]));
        await f.Transactions.CreateAsync(new TransactionInput(TransactionType.Transfer, 15000, new(2026, 9, 5), f.Checking.Id, f.Savings.Id, f.Cat["Emergency Fund"]));

        var month = await f.Budgets.GetMonthAsync(2026, 9);
        Assert.Equal(0, month.ActualCents);
    }

    [Fact]
    public async Task Card_purchase_counts_once_and_the_payment_not_at_all()
    {
        await using var f = await CreateAsync();
        await f.SpendAsync(5000, "Groceries", new(2026, 9, 5), f.Card);
        await f.Transactions.CreateAsync(new TransactionInput(TransactionType.Transfer, 5000, new(2026, 9, 20), f.Checking.Id, f.Card.Id));

        Assert.Equal(5000, (await f.Budgets.GetMonthAsync(2026, 9)).ActualCents);
    }

    [Fact]
    public async Task Month_boundaries_are_exact()
    {
        await using var f = await CreateAsync();
        await f.SpendAsync(1, "Gas", new(2026, 8, 31));
        await f.SpendAsync(10, "Gas", new(2026, 9, 1));
        await f.SpendAsync(100, "Gas", new(2026, 9, 30));
        await f.SpendAsync(1000, "Gas", new(2026, 10, 1));

        Assert.Equal(110, Line(await f.Budgets.GetMonthAsync(2026, 9), "Transportation").ActualCents);
    }

    [Fact]
    public async Task February_in_leap_and_common_years()
    {
        await using var f = await CreateAsync();
        await f.SpendAsync(100, "Gas", new(2028, 2, 29));
        await f.SpendAsync(100, "Gas", new(2027, 2, 28));
        await f.SpendAsync(999, "Gas", new(2027, 3, 1));

        Assert.Equal(100, Line(await f.Budgets.GetMonthAsync(2028, 2), "Transportation").ActualCents);
        Assert.Equal(100, Line(await f.Budgets.GetMonthAsync(2027, 2), "Transportation").ActualCents);
        Assert.Equal((new DateOnly(2028, 2, 1), new DateOnly(2028, 2, 29)), BudgetService.MonthRange(2028, 2));
    }

    [Fact]
    public async Task Year_boundary()
    {
        await using var f = await CreateAsync();
        await f.SpendAsync(100, "Gas", new(2026, 12, 31));
        await f.SpendAsync(200, "Gas", new(2027, 1, 1));

        Assert.Equal(100, (await f.Budgets.GetMonthAsync(2026, 12)).ActualCents);
        Assert.Equal(200, (await f.Budgets.GetMonthAsync(2027, 1)).ActualCents);
    }

    [Fact]
    public async Task Deleted_transactions_are_excluded()
    {
        await using var f = await CreateAsync();
        var t = await f.Transactions.CreateAsync(new TransactionInput(TransactionType.Expense, 4000, new(2026, 9, 5), f.Checking.Id, CategoryId: f.Cat["Gas"]));
        await f.Transactions.DeleteAsync(t.Id);
        Assert.Equal(0, (await f.Budgets.GetMonthAsync(2026, 9)).ActualCents);
    }

    [Fact]
    public async Task Uncategorised_spending_is_shown_not_hidden()
    {
        await using var f = await CreateAsync();
        await f.Transactions.CreateAsync(new TransactionInput(TransactionType.Expense, 1234, new(2026, 9, 5), f.Checking.Id));

        var month = await f.Budgets.GetMonthAsync(2026, 9);
        var line = month.Lines.Single(l => l.IsUncategorized);
        Assert.Equal(1234, line.ActualCents);
        Assert.Equal(1234, month.ActualCents);
    }

    [Fact]
    public async Task Month_totals_include_unplanned_spending()
    {
        await using var f = await CreateAsync();
        await f.Budgets.SetPlannedAsync(2026, 9, f.Cat["Food"], 25000);
        await f.Budgets.SetPlannedAsync(2026, 9, f.Cat["Transportation"], 30000);
        await f.SpendAsync(18700, "Groceries", new(2026, 9, 5));
        await f.SpendAsync(22100, "Gas", new(2026, 9, 6));
        await f.SpendAsync(3800, "Software", new(2026, 9, 7)); // Technology: no plan

        var month = await f.Budgets.GetMonthAsync(2026, 9);
        Assert.Equal(55000, month.PlannedCents);
        Assert.Equal(44600, month.ActualCents);
        Assert.Equal(10400, month.RemainingCents);
        Assert.Equal(BudgetLineStatus.NoPlan, Line(month, "Technology").Status);
        Assert.True(month.HasPlan);
    }

    [Fact]
    public async Task Every_active_spending_group_is_listed_so_it_can_be_planned()
    {
        await using var f = await CreateAsync();
        var month = await f.Budgets.GetMonthAsync(2026, 9);
        Assert.Contains(month.Lines, l => l.Name == "Health");
        Assert.DoesNotContain(month.Lines, l => l.Name == "Income" || l.Name == "Savings");
        Assert.False(month.HasPlan);
    }

    [Fact]
    public async Task Archived_groups_appear_only_when_they_have_a_plan_or_spending()
    {
        await using var f = await CreateAsync();
        var categories = new CategoryService(f.Db.Factory);
        await f.SpendAsync(500, "Clothing", new(2026, 9, 3));
        await categories.SetArchivedAsync(f.Cat["Personal"], true);
        await categories.SetArchivedAsync(f.Cat["Health"], true);

        var month = await f.Budgets.GetMonthAsync(2026, 9);
        Assert.True(Line(month, "Personal").IsArchived);
        Assert.DoesNotContain(month.Lines, l => l.Name == "Health");
        Assert.Equal(500, month.ActualCents);
    }

    [Fact]
    public async Task Editing_a_plan_never_changes_actual_spending()
    {
        await using var f = await CreateAsync();
        await f.SpendAsync(18700, "Groceries", new(2026, 9, 5));
        await f.Budgets.SetPlannedAsync(2026, 9, f.Cat["Food"], 25000);
        await f.Budgets.SetPlannedAsync(2026, 9, f.Cat["Food"], 10000);

        var food = Line(await f.Budgets.GetMonthAsync(2026, 9), "Food");
        Assert.Equal(18700, food.ActualCents);
        Assert.Equal(-8700, food.RemainingCents);
        Assert.Equal(BudgetLineStatus.Over, food.Status);
        await using var db = f.Db.Context();
        Assert.Equal(1, await db.Transactions.CountAsync());
    }

    [Fact]
    public async Task Setting_zero_removes_the_line_from_the_plan()
    {
        await using var f = await CreateAsync();
        await f.Budgets.SetPlannedAsync(2026, 9, f.Cat["Food"], 25000);
        await f.Budgets.SetPlannedAsync(2026, 9, f.Cat["Food"], 0);

        var month = await f.Budgets.GetMonthAsync(2026, 9);
        Assert.False(month.HasPlan);
        await using var db = f.Db.Context();
        Assert.Equal(0, await db.BudgetItems.CountAsync());
    }

    [Fact]
    public async Task Plans_are_only_for_top_level_spending_groups()
    {
        await using var f = await CreateAsync();
        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Budgets.SetPlannedAsync(2026, 9, f.Cat["Groceries"], 100));
        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Budgets.SetPlannedAsync(2026, 9, f.Cat["Income"], 100));
        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Budgets.SetPlannedAsync(2026, 9, 99999, 100));
    }

    [Theory]
    [InlineData(2026, 0)]
    [InlineData(2026, 13)]
    [InlineData(1899, 5)]
    public async Task Invalid_months_are_rejected(int year, int month)
    {
        await using var f = await CreateAsync();
        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Budgets.SetPlannedAsync(year, month, f.Cat["Food"], 100));
    }

    [Fact]
    public async Task Negative_or_huge_plans_are_rejected()
    {
        await using var f = await CreateAsync();
        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Budgets.SetPlannedAsync(2026, 9, f.Cat["Food"], -1));
        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Budgets.SetPlannedAsync(2026, 9, f.Cat["Food"], Money.MaxAmountCents + 1));
    }

    [Fact]
    public async Task Plans_are_separate_per_month()
    {
        await using var f = await CreateAsync();
        await f.Budgets.SetPlannedAsync(2026, 9, f.Cat["Food"], 25000);
        await f.Budgets.SetPlannedAsync(2026, 10, f.Cat["Food"], 30000);

        Assert.Equal(25000, Line(await f.Budgets.GetMonthAsync(2026, 9), "Food").PlannedCents);
        Assert.Equal(30000, Line(await f.Budgets.GetMonthAsync(2026, 10), "Food").PlannedCents);
    }

    [Fact]
    public async Task Copy_previous_plan_uses_the_latest_earlier_month_and_skips_archived_groups()
    {
        await using var f = await CreateAsync();
        await f.Budgets.SetPlannedAsync(2026, 6, f.Cat["Food"], 1);
        await f.Budgets.SetPlannedAsync(2026, 8, f.Cat["Food"], 25000);
        await f.Budgets.SetPlannedAsync(2026, 8, f.Cat["Health"], 5000);
        await new CategoryService(f.Db.Factory).SetArchivedAsync(f.Cat["Health"], true);

        Assert.True((await f.Budgets.GetMonthAsync(2026, 10)).CanCopyPreviousPlan);
        Assert.Equal(1, await f.Budgets.CopyPreviousPlanAsync(2026, 10));

        var october = await f.Budgets.GetMonthAsync(2026, 10);
        Assert.Equal(25000, Line(october, "Food").PlannedCents);
        Assert.Equal(25000, october.PlannedCents);
        Assert.False(october.CanCopyPreviousPlan);
    }

    [Fact]
    public async Task Copy_never_overwrites_an_existing_plan()
    {
        await using var f = await CreateAsync();
        await f.Budgets.SetPlannedAsync(2026, 8, f.Cat["Food"], 25000);
        await f.Budgets.SetPlannedAsync(2026, 9, f.Cat["Food"], 100);

        Assert.Equal(0, await f.Budgets.CopyPreviousPlanAsync(2026, 9));
        Assert.Equal(100, Line(await f.Budgets.GetMonthAsync(2026, 9), "Food").PlannedCents);
    }

    [Fact]
    public async Task Copy_with_nothing_earlier_does_nothing()
    {
        await using var f = await CreateAsync();
        Assert.False((await f.Budgets.GetMonthAsync(2026, 9)).CanCopyPreviousPlan);
        Assert.Equal(0, await f.Budgets.CopyPreviousPlanAsync(2026, 9));
    }

    [Theory]
    [InlineData(0, 0, BudgetLineStatus.NoPlan)]
    [InlineData(0, 500, BudgetLineStatus.NoPlan)]
    [InlineData(10000, 0, BudgetLineStatus.OnTrack)]
    [InlineData(10000, 8999, BudgetLineStatus.OnTrack)]
    [InlineData(10000, 9000, BudgetLineStatus.NearLimit)]
    [InlineData(10000, 10000, BudgetLineStatus.NearLimit)]
    [InlineData(10000, 10001, BudgetLineStatus.Over)]
    public void Status_thresholds(long planned, long actual, BudgetLineStatus expected) =>
        Assert.Equal(expected, BudgetService.StatusFor(planned, actual));

    [Theory]
    [InlineData(25000, 18700, 0.748)]
    [InlineData(25000, 99999, 1.0)]  // over budget never exceeds a full bar
    [InlineData(25000, 0, 0.0)]
    [InlineData(0, 0, 0.0)]          // no divide-by-zero
    [InlineData(0, 100, 1.0)]
    public void Fraction_used_is_always_between_zero_and_one(long planned, long actual, double expected) =>
        Assert.Equal(expected, BudgetService.FractionUsed(planned, actual), 3);
}

using Ledger.Core.Models;
using Ledger.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Core.Tests;

public class TransactionServiceTests
{
    private static readonly DateOnly Today = new(2026, 9, 26);

    private sealed class Fixture : IAsyncDisposable
    {
        public required TestDatabase Db { get; init; }
        public required TransactionService Transactions { get; init; }
        public required AccountService Accounts { get; init; }
        public required Account Checking { get; init; }
        public required Account Savings { get; init; }
        public required Account Card { get; init; }
        public required Dictionary<string, int> Cat { get; init; }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private static async Task<Fixture> CreateAsync()
    {
        var db = await TestDatabase.CreateAsync(Today);
        var checking = await db.AddAccountAsync("Checking", AccountType.Checking, 100_000);
        var savings = await db.AddAccountAsync("Savings", AccountType.Savings);
        var card = await db.AddAccountAsync("Visa", AccountType.CreditCard);
        await using var ctx = db.Context();
        return new Fixture
        {
            Db = db,
            Transactions = new TransactionService(db.Factory, db.Time),
            Accounts = new AccountService(db.Factory, db.Time),
            Checking = checking, Savings = savings, Card = card,
            Cat = (await ctx.Categories.ToListAsync()).GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.First().Id),
        };
    }

    private static TransactionInput Expense(Account a, long cents, int? category = null, DateOnly? date = null, string? payee = null) =>
        new(TransactionType.Expense, cents, date ?? Today, a.Id, CategoryId: category, Payee: payee);

    private static TransactionInput Income(Account a, long cents, int? category = null, DateOnly? date = null, string? payee = null) =>
        new(TransactionType.Income, cents, date ?? Today, a.Id, CategoryId: category, Payee: payee);

    private static TransactionInput Transfer(Account from, Account to, long cents, DateOnly? date = null) =>
        new(TransactionType.Transfer, cents, date ?? Today, from.Id, to.Id);

    // ---- Recording --------------------------------------------------------------------------

    [Fact]
    public async Task Recorded_transactions_change_balances()
    {
        await using var f = await CreateAsync();
        await f.Transactions.CreateAsync(Expense(f.Checking, 842, f.Cat["Groceries"], payee: "Quickmart"));
        await f.Transactions.CreateAsync(Income(f.Checking, 18472, f.Cat["Paycheck"], payee: "Riverside Market"));

        Assert.Equal(100_000 - 842 + 18472, await f.Accounts.GetBalanceAsync(f.Checking.Id));
    }

    [Fact]
    public async Task Income_and_transfers_are_not_spending()
    {
        await using var f = await CreateAsync();
        await f.Transactions.CreateAsync(Income(f.Checking, 61240, f.Cat["Paycheck"]));
        await f.Transactions.CreateAsync(Transfer(f.Checking, f.Savings, 15000));
        await f.Transactions.CreateAsync(Expense(f.Checking, 2499, f.Cat["Hardware"]));

        var totals = await f.Transactions.GetTotalsAsync(new TransactionQuery());
        Assert.Equal(3, totals.Count);
        Assert.Equal(61240, totals.IncomeCents);
        Assert.Equal(2499, totals.ExpenseCents);
    }

    [Fact]
    public async Task Card_purchase_then_payment_is_fifty_dollars_of_spending_not_one_hundred()
    {
        await using var f = await CreateAsync();
        await f.Transactions.CreateAsync(Expense(f.Card, 5000, f.Cat["Groceries"]));
        await f.Transactions.CreateAsync(Transfer(f.Checking, f.Card, 5000));

        var totals = await f.Transactions.GetTotalsAsync(new TransactionQuery());
        Assert.Equal(5000, totals.ExpenseCents);
        Assert.Equal(0, await f.Accounts.GetBalanceAsync(f.Card.Id));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Amount_must_be_positive(long cents)
    {
        await using var f = await CreateAsync();
        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Transactions.CreateAsync(Expense(f.Checking, cents)));
    }

    [Fact]
    public async Task Amount_over_the_cap_is_rejected()
    {
        await using var f = await CreateAsync();
        await Assert.ThrowsAsync<LedgerValidationException>(() =>
            f.Transactions.CreateAsync(Expense(f.Checking, Money.MaxAmountCents + 1)));
    }

    [Fact]
    public async Task Transfer_needs_a_different_destination()
    {
        await using var f = await CreateAsync();
        await Assert.ThrowsAsync<LedgerValidationException>(() =>
            f.Transactions.CreateAsync(new TransactionInput(TransactionType.Transfer, 100, Today, f.Checking.Id)));
        await Assert.ThrowsAsync<LedgerValidationException>(() =>
            f.Transactions.CreateAsync(Transfer(f.Checking, f.Checking, 100)));
    }

    [Fact]
    public async Task Destination_is_dropped_for_non_transfers()
    {
        await using var f = await CreateAsync();
        var t = await f.Transactions.CreateAsync(
            new TransactionInput(TransactionType.Expense, 100, Today, f.Checking.Id, TransferAccountId: f.Savings.Id));
        Assert.Null(t.TransferAccountId);
    }

    [Fact]
    public async Task Category_must_match_the_kind_of_transaction()
    {
        await using var f = await CreateAsync();
        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Transactions.CreateAsync(Expense(f.Checking, 100, f.Cat["Paycheck"])));
        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Transactions.CreateAsync(Income(f.Checking, 100, f.Cat["Groceries"])));
        await Assert.ThrowsAsync<LedgerValidationException>(() =>
            f.Transactions.CreateAsync(Transfer(f.Checking, f.Savings, 100) with { CategoryId = f.Cat["Groceries"] }));

        // A savings category on a transfer is fine.
        await f.Transactions.CreateAsync(Transfer(f.Checking, f.Savings, 100) with { CategoryId = f.Cat["Emergency Fund"] });
    }

    [Fact]
    public async Task Archived_accounts_and_categories_cannot_be_newly_used_but_existing_entries_can_be_edited()
    {
        await using var f = await CreateAsync();
        var t = await f.Transactions.CreateAsync(Expense(f.Card, 1000, f.Cat["Groceries"]));
        await f.Accounts.SetArchivedAsync(f.Card.Id, true);
        await new CategoryService(f.Db.Factory).SetArchivedAsync(f.Cat["Groceries"], true);

        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Transactions.CreateAsync(Expense(f.Card, 1000)));
        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Transactions.CreateAsync(Expense(f.Checking, 1000, f.Cat["Groceries"])));

        // Editing the amount of the old entry keeps its archived account and category.
        await f.Transactions.UpdateAsync(t.Id, Expense(f.Card, 1200, f.Cat["Groceries"]));
        Assert.Equal(1200, (await f.Transactions.GetAsync(t.Id)).AmountCents);
    }

    [Fact]
    public async Task Dates_outside_a_sensible_range_are_rejected()
    {
        await using var f = await CreateAsync();
        await Assert.ThrowsAsync<LedgerValidationException>(() =>
            f.Transactions.CreateAsync(Expense(f.Checking, 100, date: new DateOnly(1899, 12, 31))));
        await Assert.ThrowsAsync<LedgerValidationException>(() =>
            f.Transactions.CreateAsync(Expense(f.Checking, 100, date: new DateOnly(2101, 1, 1))));
    }

    [Fact]
    public async Task Payee_and_memo_are_trimmed_and_blank_becomes_null()
    {
        await using var f = await CreateAsync();
        var t = await f.Transactions.CreateAsync(Expense(f.Checking, 100) with { Payee = "  Amazon ", Memo = "   " });
        Assert.Equal("Amazon", t.Payee);
        Assert.Null(t.Memo);
    }

    [Fact]
    public async Task Update_changes_type_and_clears_transfer_destination()
    {
        await using var f = await CreateAsync();
        var t = await f.Transactions.CreateAsync(Transfer(f.Checking, f.Savings, 5000));
        await f.Transactions.UpdateAsync(t.Id, Expense(f.Checking, 5000, f.Cat["Groceries"]));

        var updated = await f.Transactions.GetAsync(t.Id);
        Assert.Equal(TransactionType.Expense, updated.TransactionType);
        Assert.Null(updated.TransferAccountId);
        Assert.Equal(0, await f.Accounts.GetBalanceAsync(f.Savings.Id));
    }

    // ---- Delete / undo ------------------------------------------------------------------------

    [Fact]
    public async Task Delete_hides_the_transaction_and_restore_brings_it_back()
    {
        await using var f = await CreateAsync();
        var t = await f.Transactions.CreateAsync(Expense(f.Checking, 2000));

        await f.Transactions.DeleteAsync(t.Id);
        Assert.Empty(await f.Transactions.QueryAsync(new TransactionQuery()));
        Assert.Equal(100_000, await f.Accounts.GetBalanceAsync(f.Checking.Id));

        await f.Transactions.RestoreAsync(t.Id);
        Assert.Single(await f.Transactions.QueryAsync(new TransactionQuery()));
        Assert.Equal(98_000, await f.Accounts.GetBalanceAsync(f.Checking.Id));
    }

    [Fact]
    public async Task Deleted_rows_are_kept_on_disk_with_a_timestamp()
    {
        await using var f = await CreateAsync();
        var t = await f.Transactions.CreateAsync(Expense(f.Checking, 2000));
        await f.Transactions.DeleteAsync(t.Id);

        await using var db = f.Db.Context();
        var row = await db.Transactions.IgnoreQueryFilters().SingleAsync();
        Assert.True(row.IsDeleted);
        Assert.NotNull(row.DeletedAt);
    }

    // ---- Querying -----------------------------------------------------------------------------

    [Fact]
    public async Task Results_are_newest_first()
    {
        await using var f = await CreateAsync();
        await f.Transactions.CreateAsync(Expense(f.Checking, 1, date: new(2026, 9, 1)));
        await f.Transactions.CreateAsync(Expense(f.Checking, 2, date: new(2026, 9, 20)));
        await f.Transactions.CreateAsync(Expense(f.Checking, 3, date: new(2026, 9, 20)));

        Assert.Equal([3L, 2L, 1L], (await f.Transactions.QueryAsync(new TransactionQuery())).Select(t => t.AmountCents));
    }

    [Fact]
    public async Task Search_ignores_case_and_matches_payee_memo_category_and_amount()
    {
        await using var f = await CreateAsync();
        await f.Transactions.CreateAsync(Expense(f.Checking, 842, f.Cat["Fast Food"], payee: "Quickmart"));
        await f.Transactions.CreateAsync(Expense(f.Checking, 2499, payee: "Amazon") with { Memo = "Camera accessories" });
        await f.Transactions.CreateAsync(Expense(f.Checking, 3790, f.Cat["Hardware"], payee: "Apple"));

        async Task<string[]> Find(string s) =>
            (await f.Transactions.QueryAsync(new TransactionQuery(Search: s))).Select(t => t.Payee!).Order().ToArray();

        Assert.Equal(["Quickmart"], await Find("quickmart"));
        Assert.Equal(["Amazon"], await Find("CAMERA"));
        Assert.Equal(["Quickmart"], await Find("food"));        // parent group name
        Assert.Equal(["Apple"], await Find("technology"));   // Hardware's group
        Assert.Equal(["Apple"], await Find("37.90"));        // amount
    }

    [Fact]
    public async Task Search_treats_wildcards_literally()
    {
        await using var f = await CreateAsync();
        await f.Transactions.CreateAsync(Expense(f.Checking, 100, payee: "50% off sale"));
        await f.Transactions.CreateAsync(Expense(f.Checking, 100, payee: "5000 widgets"));

        Assert.Equal(["50% off sale"], (await f.Transactions.QueryAsync(new TransactionQuery(Search: "50%"))).Select(t => t.Payee));
        Assert.Empty(await f.Transactions.QueryAsync(new TransactionQuery(Search: "_idgets")));
    }

    [Fact]
    public async Task Account_filter_includes_transfers_in_both_directions()
    {
        await using var f = await CreateAsync();
        await f.Transactions.CreateAsync(Expense(f.Checking, 100));
        await f.Transactions.CreateAsync(Transfer(f.Checking, f.Savings, 200));
        await f.Transactions.CreateAsync(Transfer(f.Savings, f.Checking, 300));
        await f.Transactions.CreateAsync(Expense(f.Card, 400));

        var savings = await f.Transactions.QueryAsync(new TransactionQuery(AccountId: f.Savings.Id));
        Assert.Equal([300L, 200L], savings.Select(t => t.AmountCents));
        Assert.Equal([-300L, 200L], savings.Select(t => t.SignedAmountFor(f.Savings.Id)!.Value));
    }

    [Fact]
    public async Task Category_filter_on_a_group_includes_its_subcategories()
    {
        await using var f = await CreateAsync();
        await f.Transactions.CreateAsync(Expense(f.Checking, 100, f.Cat["Groceries"]));
        await f.Transactions.CreateAsync(Expense(f.Checking, 200, f.Cat["Restaurants"]));
        await f.Transactions.CreateAsync(Expense(f.Checking, 300, f.Cat["Gas"]));

        Assert.Equal(2, (await f.Transactions.QueryAsync(new TransactionQuery(CategoryId: f.Cat["Food"]))).Count);
        Assert.Single(await f.Transactions.QueryAsync(new TransactionQuery(CategoryId: f.Cat["Groceries"])));
    }

    [Fact]
    public async Task Date_range_is_inclusive_at_both_ends()
    {
        await using var f = await CreateAsync();
        foreach (var d in new DateOnly[] { new(2026, 8, 31), new(2026, 9, 1), new(2026, 9, 30), new(2026, 10, 1) })
        {
            await f.Transactions.CreateAsync(Expense(f.Checking, 100, date: d));
        }
        var september = await f.Transactions.QueryAsync(new TransactionQuery(From: new(2026, 9, 1), To: new(2026, 9, 30)));
        Assert.Equal([new DateOnly(2026, 9, 30), new DateOnly(2026, 9, 1)], september.Select(t => t.Date));
    }

    [Fact]
    public async Task Type_filter_and_totals_respect_filters()
    {
        await using var f = await CreateAsync();
        await f.Transactions.CreateAsync(Expense(f.Checking, 100));
        await f.Transactions.CreateAsync(Income(f.Checking, 500));
        await f.Transactions.CreateAsync(Transfer(f.Checking, f.Savings, 900));

        Assert.Single(await f.Transactions.QueryAsync(new TransactionQuery(Type: TransactionType.Transfer)));
        var incomeOnly = await f.Transactions.GetTotalsAsync(new TransactionQuery(Type: TransactionType.Income));
        Assert.Equal(new TransactionTotals(1, 500, 0), incomeOnly);
    }

    [Fact]
    public async Task List_items_carry_display_names()
    {
        await using var f = await CreateAsync();
        await f.Transactions.CreateAsync(Expense(f.Checking, 100, f.Cat["Groceries"]));
        await f.Transactions.CreateAsync(Transfer(f.Checking, f.Savings, 100));

        var items = await f.Transactions.QueryAsync(new TransactionQuery());
        var transfer = items.Single(i => i.Type == TransactionType.Transfer);
        Assert.Equal(("Checking", "Savings"), (transfer.AccountName, transfer.TransferAccountName));
        Assert.Null(transfer.SignedAmountFor(null));
        Assert.Equal("Food › Groceries", items.Single(i => i.Type == TransactionType.Expense).CategoryFullName);
    }

    [Fact]
    public async Task Payee_suggestions_are_distinct_recent_and_carry_the_last_category()
    {
        await using var f = await CreateAsync();
        await f.Transactions.CreateAsync(Expense(f.Checking, 100, f.Cat["Groceries"], new(2026, 9, 1), "Shopwise"));
        await f.Transactions.CreateAsync(Expense(f.Checking, 100, f.Cat["Fast Food"], new(2026, 9, 20), "shopwise"));
        await f.Transactions.CreateAsync(Expense(f.Checking, 100, null, new(2026, 9, 10), "Shell"));
        await f.Transactions.CreateAsync(Income(f.Checking, 100, null, new(2026, 9, 10), "Sheriff refund"));

        var suggestions = await f.Transactions.GetPayeeSuggestionsAsync("sh", TransactionType.Expense);
        Assert.Equal(["shopwise", "Shell"], suggestions.Select(s => s.Payee));
        Assert.Equal(f.Cat["Fast Food"], suggestions[0].CategoryId);
    }
}

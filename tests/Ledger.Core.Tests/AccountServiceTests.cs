using Ledger.Core.Models;
using Ledger.Core.Services;

namespace Ledger.Core.Tests;

public class AccountServiceTests
{
    private static readonly DateOnly Today = new(2026, 9, 26);

    private static AccountService Service(TestDatabase t) => new(t.Factory, t.Time);

    [Fact]
    public async Task Balance_is_starting_balance_when_there_is_no_history()
    {
        await using var t = await TestDatabase.CreateAsync(Today);
        var checking = await t.AddAccountAsync("Checking", AccountType.Checking, 64218);

        Assert.Equal(64218, await Service(t).GetBalanceAsync(checking.Id));
    }

    [Fact]
    public async Task Income_adds_and_expenses_subtract()
    {
        await using var t = await TestDatabase.CreateAsync(Today);
        var checking = await t.AddAccountAsync("Checking", AccountType.Checking, 10000);
        await t.AddTransactionAsync(Income(checking, 18472));
        await t.AddTransactionAsync(Expense(checking, 842));

        Assert.Equal(10000 + 18472 - 842, await Service(t).GetBalanceAsync(checking.Id));
    }

    [Fact]
    public async Task Transfer_moves_money_without_changing_net_worth()
    {
        await using var t = await TestDatabase.CreateAsync(Today);
        var checking = await t.AddAccountAsync("Checking", AccountType.Checking, 50000);
        var savings = await t.AddAccountAsync("Savings", AccountType.Savings, 0);
        var service = Service(t);
        var before = await service.GetNetWorthAsync();

        await t.AddTransactionAsync(Transfer(checking, savings, 10000));

        Assert.Equal(40000, await service.GetBalanceAsync(checking.Id));
        Assert.Equal(10000, await service.GetBalanceAsync(savings.Id));
        Assert.Equal(before.NetWorthCents, (await service.GetNetWorthAsync()).NetWorthCents);
    }

    [Fact]
    public async Task Credit_card_purchase_and_payment_are_counted_once()
    {
        await using var t = await TestDatabase.CreateAsync(Today);
        var checking = await t.AddAccountAsync("Checking", AccountType.Checking, 50000);
        var card = await t.AddAccountAsync("Credit Card", AccountType.CreditCard, 0);
        var service = Service(t);

        await t.AddTransactionAsync(Expense(card, 5000));
        var afterPurchase = await service.GetNetWorthAsync();
        Assert.Equal(-5000, await service.GetBalanceAsync(card.Id));
        Assert.Equal(5000, afterPurchase.LiabilitiesCents);
        Assert.Equal(45000, afterPurchase.NetWorthCents);

        await t.AddTransactionAsync(Transfer(checking, card, 5000));
        var afterPayment = await service.GetNetWorthAsync();
        Assert.Equal(0, await service.GetBalanceAsync(card.Id));
        Assert.Equal(45000, await service.GetBalanceAsync(checking.Id));
        // Paying the card only moves money: net worth is unchanged by the payment.
        Assert.Equal(afterPurchase.NetWorthCents, afterPayment.NetWorthCents);
    }

    [Fact]
    public async Task Future_dated_transactions_are_not_part_of_the_actual_balance()
    {
        await using var t = await TestDatabase.CreateAsync(Today);
        var checking = await t.AddAccountAsync("Checking", AccountType.Checking, 10000);
        await t.AddTransactionAsync(Expense(checking, 2000, Today.AddDays(1)));

        var service = Service(t);
        Assert.Equal(10000, await service.GetBalanceAsync(checking.Id));
        Assert.Equal(8000, await service.GetBalanceAsync(checking.Id, Today.AddDays(1)));
    }

    [Fact]
    public async Task Soft_deleted_transactions_do_not_affect_balance()
    {
        await using var t = await TestDatabase.CreateAsync(Today);
        var checking = await t.AddAccountAsync("Checking", AccountType.Checking, 10000);
        var tx = Expense(checking, 2000);
        tx.IsDeleted = true;
        await t.AddTransactionAsync(tx);

        Assert.Equal(10000, await Service(t).GetBalanceAsync(checking.Id));
    }

    [Fact]
    public async Task Net_worth_excludes_accounts_flagged_out_but_keeps_archived_ones()
    {
        await using var t = await TestDatabase.CreateAsync(Today);
        var service = Service(t);
        await service.CreateAsync(new AccountInput("Checking", AccountType.Checking, 10000));
        await service.CreateAsync(new AccountInput("Tracking only", AccountType.Other, 99999, IsIncludedInNetWorth: false));
        var old = await service.CreateAsync(new AccountInput("Old savings", AccountType.Savings, 500));
        await service.SetArchivedAsync(old.Id, true);

        Assert.Equal(10500, (await service.GetNetWorthAsync()).NetWorthCents);
        Assert.Equal(2, (await service.GetAccountBalancesAsync()).Count);
        Assert.Equal(3, (await service.GetAccountBalancesAsync(includeArchived: true)).Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_rejects_blank_names(string name)
    {
        await using var t = await TestDatabase.CreateAsync(Today);
        await Assert.ThrowsAsync<LedgerValidationException>(() =>
            Service(t).CreateAsync(new AccountInput(name, AccountType.Checking, 0)));
    }

    [Fact]
    public async Task Create_trims_name()
    {
        await using var t = await TestDatabase.CreateAsync(Today);
        var account = await Service(t).CreateAsync(new AccountInput("  Checking  ", AccountType.Checking, 0));
        Assert.Equal("Checking", account.Name);
    }

    [Fact]
    public async Task Create_rejects_absurd_starting_balances()
    {
        await using var t = await TestDatabase.CreateAsync(Today);
        await Assert.ThrowsAsync<LedgerValidationException>(() =>
            Service(t).CreateAsync(new AccountInput("Checking", AccountType.Checking, Money.MaxAmountCents + 1)));
        await Assert.ThrowsAsync<LedgerValidationException>(() =>
            Service(t).CreateAsync(new AccountInput("Card", AccountType.CreditCard, -Money.MaxAmountCents - 1)));
    }

    [Fact]
    public async Task Update_changes_details_and_rebases_balance_without_touching_history()
    {
        await using var t = await TestDatabase.CreateAsync(Today);
        var service = Service(t);
        var account = await service.CreateAsync(new AccountInput("Checking", AccountType.Checking, 10000));
        await t.AddTransactionAsync(Expense(account, 2500));

        await service.UpdateAsync(account.Id, new AccountInput("Everyday", AccountType.Checking, 20000, "Credit Union"));

        var row = (await service.GetAccountBalancesAsync()).Single();
        Assert.Equal("Everyday", row.Account.Name);
        Assert.Equal("Credit Union", row.Account.InstitutionName);
        Assert.Equal(20000 - 2500, row.BalanceCents);
        await using var db = t.Context();
        Assert.Equal(1, db.Transactions.Count());
    }

    [Fact]
    public async Task Update_of_missing_account_is_a_friendly_error()
    {
        await using var t = await TestDatabase.CreateAsync(Today);
        await Assert.ThrowsAsync<LedgerValidationException>(() =>
            Service(t).UpdateAsync(404, new AccountInput("X", AccountType.Cash, 0)));
    }

    [Fact]
    public async Task Archiving_hides_an_account_and_restoring_brings_it_back_with_its_balance()
    {
        await using var t = await TestDatabase.CreateAsync(Today);
        var service = Service(t);
        var account = await service.CreateAsync(new AccountInput("Cash", AccountType.Cash, 4500));
        await t.AddTransactionAsync(Expense(account, 500));

        await service.SetArchivedAsync(account.Id, true);
        Assert.Empty(await service.GetAccountBalancesAsync());

        await service.SetArchivedAsync(account.Id, false);
        var restored = Assert.Single(await service.GetAccountBalancesAsync());
        Assert.Equal(4000, restored.BalanceCents);
    }

    [Fact]
    public async Task New_accounts_are_ordered_after_existing_ones()
    {
        await using var t = await TestDatabase.CreateAsync(Today);
        var service = Service(t);
        await service.CreateAsync(new AccountInput("Zeta", AccountType.Checking, 0));
        await service.CreateAsync(new AccountInput("Alpha", AccountType.Savings, 0));

        Assert.Equal(["Zeta", "Alpha"], (await service.GetAccountBalancesAsync()).Select(b => b.Account.Name));
    }

    [Fact]
    public async Task Credit_card_entered_as_amount_owed_is_a_liability()
    {
        await using var t = await TestDatabase.CreateAsync(Today);
        var service = Service(t);
        var stored = AccountType.CreditCard.ToStoredBalance(12000);
        await service.CreateAsync(new AccountInput("Card", AccountType.CreditCard, stored));
        await service.CreateAsync(new AccountInput("Checking", AccountType.Checking, 64218));

        var worth = await service.GetNetWorthAsync();
        Assert.Equal(64218, worth.AssetsCents);
        Assert.Equal(12000, worth.LiabilitiesCents);
        Assert.Equal(52218, worth.NetWorthCents);
    }

    private static Transaction Income(Account a, long cents) => new()
    {
        AccountId = a.Id, AmountCents = cents, TransactionType = TransactionType.Income, Date = Today,
    };

    private static Transaction Expense(Account a, long cents, DateOnly? date = null) => new()
    {
        AccountId = a.Id, AmountCents = cents, TransactionType = TransactionType.Expense, Date = date ?? Today,
    };

    private static Transaction Transfer(Account from, Account to, long cents) => new()
    {
        AccountId = from.Id, TransferAccountId = to.Id, AmountCents = cents,
        TransactionType = TransactionType.Transfer, Date = Today,
    };
}

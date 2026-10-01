using Ledger.Core.Models;

namespace Ledger.Core.Tests;

public class AccountTypeRulesTests
{
    [Theory]
    [InlineData(AccountType.Checking, false)]
    [InlineData(AccountType.Savings, false)]
    [InlineData(AccountType.Cash, false)]
    [InlineData(AccountType.Other, false)]
    [InlineData(AccountType.CreditCard, true)]
    public void Only_credit_cards_are_liabilities(AccountType type, bool expected) =>
        Assert.Equal(expected, type.IsLiability());

    [Fact]
    public void Amount_owed_on_a_card_is_stored_as_a_negative_balance()
    {
        Assert.Equal(-12000, AccountType.CreditCard.ToStoredBalance(12000));
        Assert.Equal(12000, AccountType.CreditCard.ToEnteredBalance(-12000));
    }

    [Fact]
    public void A_card_in_credit_is_stored_as_positive() =>
        Assert.Equal(500, AccountType.CreditCard.ToStoredBalance(-500));

    [Fact]
    public void Asset_balances_are_stored_as_entered()
    {
        Assert.Equal(64218, AccountType.Checking.ToStoredBalance(64218));
        Assert.Equal(-2000, AccountType.Checking.ToStoredBalance(-2000)); // overdrawn
    }

    [Theory]
    [InlineData(AccountType.Checking)]
    [InlineData(AccountType.CreditCard)]
    public void Conversion_round_trips(AccountType type)
    {
        foreach (var cents in new long[] { 0, 1, -1, 12345, -Money.MaxAmountCents, Money.MaxAmountCents })
        {
            Assert.Equal(cents, type.ToEnteredBalance(type.ToStoredBalance(cents)));
        }
    }
}

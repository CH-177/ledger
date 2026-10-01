using Ledger.Core.Models;

namespace Ledger.App.Presentation;

/// <summary>How an account type is presented: its label and Segoe Fluent icon.</summary>
public sealed record AccountTypeOption(AccountType Type, string Name, string Glyph)
{
    public override string ToString() => Name;
}

public static class AccountTypeDisplay
{
    public static IReadOnlyList<AccountTypeOption> All { get; } =
    [
        new(AccountType.Checking, "Checking", ""),
        new(AccountType.Savings, "Savings", ""),
        new(AccountType.Cash, "Cash", ""),
        new(AccountType.CreditCard, "Credit card", ""),
        new(AccountType.Other, "Other", ""),
    ];

    public static AccountTypeOption For(AccountType type) =>
        All.FirstOrDefault(o => o.Type == type) ?? All[^1];

    public static int IndexOf(AccountType type) =>
        Math.Max(0, All.ToList().FindIndex(o => o.Type == type));
}

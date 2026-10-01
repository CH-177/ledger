using Ledger.Core.Models;

namespace Ledger.Core.Data;

/// <summary>The category tree created on first launch.</summary>
internal static class DefaultCategories
{
    public sealed record Seed(string Name, CategoryType Type, string Icon, string[] Children);

    // Icons are Segoe Fluent Icons code points.
    public static readonly Seed[] All =
    [
        new("Housing", CategoryType.Expense, "", ["Rent", "Utilities", "Internet", "Phone"]),
        new("Food", CategoryType.Expense, "", ["Groceries", "Restaurants", "Fast Food"]),
        new("Transportation", CategoryType.Expense, "", ["Gas", "Auto Insurance", "Maintenance", "Parking"]),
        new("Technology", CategoryType.Expense, "", ["Software", "Hardware", "Subscriptions"]),
        new("Entertainment", CategoryType.Expense, "", []),
        new("Health", CategoryType.Expense, "", []),
        new("Personal", CategoryType.Expense, "", ["Clothing", "Gifts"]),
        new("Savings", CategoryType.Savings, "", ["Emergency Fund", "Car", "Computer"]),
        new("Income", CategoryType.Income, "", ["Paycheck", "Freelance", "Other"]),
    ];
}

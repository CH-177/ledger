using Ledger.Core.Models;

namespace Ledger.App.Presentation;

public sealed record CategoryKindOption(CategoryType Type, string Name, string Description)
{
    public override string ToString() => Name;
}

/// <summary>How category types are labelled, and the order their sections appear in.</summary>
public static class CategoryDisplay
{
    /// <summary>Used for groups that have no icon of their own (e.g. ones the user created).</summary>
    public const string DefaultGlyph = "";

    public static IReadOnlyList<CategoryKindOption> Kinds { get; } =
    [
        new(CategoryType.Expense, "Spending", "Where your money goes."),
        new(CategoryType.Income, "Income", "Where your money comes from."),
        new(CategoryType.Savings, "Savings", "Money set aside for goals. Never counted as spending."),
    ];

    public static CategoryKindOption For(CategoryType type) => Kinds.FirstOrDefault(k => k.Type == type) ?? Kinds[0];

    public static int IndexOf(CategoryType type) => Math.Max(0, Kinds.ToList().FindIndex(k => k.Type == type));
}

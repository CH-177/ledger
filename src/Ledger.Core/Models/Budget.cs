namespace Ledger.Core.Models;

/// <summary>Planned spending for one calendar month. Unique per (Year, Month).</summary>
public class Budget : IAuditable
{
    public int Id { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public List<BudgetItem> Items { get; set; } = [];
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>The planned amount for one category within a <see cref="Budget"/>.</summary>
public class BudgetItem
{
    public int Id { get; set; }

    public int BudgetId { get; set; }
    public Budget? Budget { get; set; }

    public int CategoryId { get; set; }
    public Category? Category { get; set; }

    public long PlannedAmountCents { get; set; }
}

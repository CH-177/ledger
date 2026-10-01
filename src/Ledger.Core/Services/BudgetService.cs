using Ledger.Core.Data;
using Ledger.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Core.Services;

/// <summary>How a budget line is doing. Always shown with words, never colour alone.</summary>
public enum BudgetLineStatus
{
    /// <summary>Nothing planned for this category this month.</summary>
    NoPlan,
    OnTrack,

    /// <summary>At or above <see cref="BudgetService.NearLimitThreshold"/> of the plan.</summary>
    NearLimit,
    Over,
}

/// <summary>Planned versus actual spending for one spending group in one month.</summary>
public sealed record BudgetLine(
    int? CategoryId,
    string Name,
    string? Icon,
    long PlannedCents,
    long ActualCents,
    bool IsArchived)
{
    /// <summary>Planned minus actual. Negative when over.</summary>
    public long RemainingCents => PlannedCents - ActualCents;

    /// <summary>True for spending recorded without a category, which cannot be planned.</summary>
    public bool IsUncategorized => CategoryId is null;

    public BudgetLineStatus Status => BudgetService.StatusFor(PlannedCents, ActualCents);

    /// <summary>Share of the plan used, clamped to 0–1 for a progress bar.</summary>
    public double FractionUsed => BudgetService.FractionUsed(PlannedCents, ActualCents);
}

/// <summary>A month's plan and what actually happened.</summary>
public sealed record BudgetMonth(int Year, int Month, IReadOnlyList<BudgetLine> Lines, bool HasPlan, bool CanCopyPreviousPlan)
{
    public long PlannedCents => Lines.Sum(l => l.PlannedCents);

    /// <summary>All spending in the month, including unplanned and uncategorised spending.</summary>
    public long ActualCents => Lines.Sum(l => l.ActualCents);

    public long RemainingCents => PlannedCents - ActualCents;

    public BudgetLineStatus Status => BudgetService.StatusFor(PlannedCents, ActualCents);

    public double FractionUsed => BudgetService.FractionUsed(PlannedCents, ActualCents);
}

/// <summary>
/// Monthly spending plans. Plans are set on top-level spending groups; actual spending always
/// comes from expense transactions (subcategories roll up into their group), so editing a plan
/// never changes what was actually spent. Income and transfers never count as spending.
/// </summary>
public sealed class BudgetService(IDbContextFactory<LedgerDbContext> contextFactory)
{
    /// <summary>Share of the plan at which a line is reported as near its limit.</summary>
    public const double NearLimitThreshold = 0.9;

    public async Task<BudgetMonth> GetMonthAsync(int year, int month, CancellationToken ct = default)
    {
        var (from, to) = MonthRange(year, month);
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        var groups = await db.Categories.AsNoTracking()
            .Where(c => c.ParentCategoryId == null && c.CategoryType == CategoryType.Expense)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .ToListAsync(ct);
        var groupOf = await db.Categories.AsNoTracking()
            .Select(c => new { c.Id, GroupId = c.ParentCategoryId ?? c.Id })
            .ToDictionaryAsync(c => c.Id, c => c.GroupId, ct);

        var planned = await db.BudgetItems.AsNoTracking()
            .Where(i => i.Budget!.Year == year && i.Budget.Month == month)
            .ToDictionaryAsync(i => i.CategoryId, i => i.PlannedAmountCents, ct);

        var spentByCategory = await db.Transactions.AsNoTracking()
            .Where(t => (t.TransactionType == TransactionType.Expense || t.TransactionType == TransactionType.Refund) && t.Date >= from && t.Date <= to)
            .GroupBy(t => t.CategoryId)
            // A refund lowers what was spent in its category that month (it can go below zero).
            .Select(g => new { CategoryId = g.Key, Sum = g.Sum(t => t.TransactionType == TransactionType.Refund ? -t.AmountCents : t.AmountCents) })
            .ToListAsync(ct);

        var spentByGroup = new Dictionary<int, long>();
        long uncategorized = 0;
        foreach (var row in spentByCategory)
        {
            if (row.CategoryId is { } id && groupOf.TryGetValue(id, out var groupId))
            {
                spentByGroup[groupId] = checked(spentByGroup.GetValueOrDefault(groupId) + row.Sum);
            }
            else
            {
                uncategorized = checked(uncategorized + row.Sum);
            }
        }

        var lines = new List<BudgetLine>();
        foreach (var g in groups)
        {
            var plan = planned.GetValueOrDefault(g.Id);
            var spent = spentByGroup.GetValueOrDefault(g.Id);
            // Archived groups appear only when they matter for this month.
            if (g.IsArchived && plan == 0 && spent == 0)
            {
                continue;
            }
            lines.Add(new BudgetLine(g.Id, g.Name, g.Icon, plan, spent, g.IsArchived));
        }
        if (uncategorized > 0)
        {
            lines.Add(new BudgetLine(null, "No category", null, 0, uncategorized, false));
        }

        var canCopy = planned.Count == 0 && await FindPreviousPlanAsync(db, year, month, ct) is not null;
        return new BudgetMonth(year, month, lines, planned.Count > 0, canCopy);
    }

    /// <summary>
    /// Sets the planned amount for a spending group in a month. Zero removes the line from the plan.
    /// </summary>
    public async Task SetPlannedAsync(int year, int month, int categoryId, long plannedCents, CancellationToken ct = default)
    {
        ValidateMonth(year, month);
        if (plannedCents < 0)
        {
            throw new LedgerValidationException("A planned amount can't be negative.");
        }
        if (plannedCents > Money.MaxAmountCents)
        {
            throw new LedgerValidationException("That planned amount is too large.");
        }

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var category = await db.Categories.FindAsync([categoryId], ct)
            ?? throw new LedgerValidationException("That category no longer exists.");
        if (category.CategoryType != CategoryType.Expense || category.ParentCategoryId is not null)
        {
            throw new LedgerValidationException("Budgets are planned for spending groups, like Food or Transportation.");
        }

        var budget = await db.Budgets.Include(b => b.Items).FirstOrDefaultAsync(b => b.Year == year && b.Month == month, ct);
        var item = budget?.Items.FirstOrDefault(i => i.CategoryId == categoryId);

        if (plannedCents == 0)
        {
            if (item is not null)
            {
                db.BudgetItems.Remove(item);
                await db.SaveChangesAsync(ct);
            }
            return;
        }

        if (budget is null)
        {
            budget = new Budget { Year = year, Month = month };
            db.Budgets.Add(budget);
        }
        if (item is null)
        {
            budget.Items.Add(new BudgetItem { CategoryId = categoryId, PlannedAmountCents = plannedCents });
        }
        else
        {
            item.PlannedAmountCents = plannedCents;
        }
        if (budget.Id != 0)
        {
            // Item changes alone don't touch the budget row; mark it so UpdatedAt reflects the edit.
            db.Entry(budget).State = EntityState.Modified;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Copies the most recent earlier month's plan into an empty month. Archived groups are skipped.
    /// Returns the number of lines copied (0 if the month already has a plan or there is nothing to copy).
    /// </summary>
    public async Task<int> CopyPreviousPlanAsync(int year, int month, CancellationToken ct = default)
    {
        ValidateMonth(year, month);
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        var target = await db.Budgets.Include(b => b.Items).FirstOrDefaultAsync(b => b.Year == year && b.Month == month, ct);
        if (target is { Items.Count: > 0 })
        {
            return 0;
        }
        var source = await FindPreviousPlanAsync(db, year, month, ct);
        if (source is null)
        {
            return 0;
        }

        var archived = await db.Categories.Where(c => c.IsArchived).Select(c => c.Id).ToListAsync(ct);
        target ??= db.Budgets.Add(new Budget { Year = year, Month = month }).Entity;
        var copied = 0;
        foreach (var item in source.Items.Where(i => !archived.Contains(i.CategoryId)))
        {
            target.Items.Add(new BudgetItem { CategoryId = item.CategoryId, PlannedAmountCents = item.PlannedAmountCents });
            copied++;
        }
        await db.SaveChangesAsync(ct);
        return copied;
    }

    public static BudgetLineStatus StatusFor(long planned, long actual) => planned switch
    {
        <= 0 => BudgetLineStatus.NoPlan,
        _ when actual > planned => BudgetLineStatus.Over,
        _ when actual >= planned * NearLimitThreshold => BudgetLineStatus.NearLimit,
        _ => BudgetLineStatus.OnTrack,
    };

    public static double FractionUsed(long planned, long actual) =>
        planned <= 0 ? (actual > 0 ? 1 : 0) : Math.Clamp((double)actual / planned, 0, 1);

    /// <summary>First and last day of a calendar month.</summary>
    public static (DateOnly From, DateOnly To) MonthRange(int year, int month)
    {
        ValidateMonth(year, month);
        var from = new DateOnly(year, month, 1);
        return (from, from.AddMonths(1).AddDays(-1));
    }

    private static async Task<Budget?> FindPreviousPlanAsync(LedgerDbContext db, int year, int month, CancellationToken ct) =>
        await db.Budgets.AsNoTracking().Include(b => b.Items)
            .Where(b => b.Year < year || (b.Year == year && b.Month < month))
            .Where(b => b.Items.Any())
            .OrderByDescending(b => b.Year).ThenByDescending(b => b.Month)
            .FirstOrDefaultAsync(ct);

    private static void ValidateMonth(int year, int month)
    {
        if (month is < 1 or > 12 || year is < 1900 or > 2100)
        {
            throw new LedgerValidationException("That isn't a valid month.");
        }
    }
}

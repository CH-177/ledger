using Ledger.Core.Data;
using Ledger.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Core.Services;

/// <summary>A top-level category with its subcategories, both in display order.</summary>
public sealed record CategoryNode(Category Category, IReadOnlyList<Category> Children);

/// <summary>
/// Manages the category hierarchy. Categories are at most two levels deep
/// (group → subcategory), and a subcategory always shares its parent's type.
/// Categories are archived rather than deleted because history refers to them.
/// </summary>
public sealed class CategoryService(IDbContextFactory<LedgerDbContext> contextFactory)
{
    public const int MaxNameLength = 100;

    /// <summary>Returns the tree ordered by type, then sort order, then name.</summary>
    public async Task<IReadOnlyList<CategoryNode>> GetTreeAsync(bool includeArchived = false, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var all = await db.Categories.AsNoTracking()
            .Where(c => includeArchived || !c.IsArchived)
            .ToListAsync(ct);

        var childrenByParent = all
            .Where(c => c.ParentCategoryId is not null)
            .GroupBy(c => c.ParentCategoryId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Category>)g.OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToList());

        return all
            .Where(c => c.ParentCategoryId is null)
            .OrderBy(c => c.CategoryType).ThenBy(c => c.SortOrder).ThenBy(c => c.Name)
            .Select(c => new CategoryNode(c, childrenByParent.GetValueOrDefault(c.Id) ?? []))
            .ToList();
    }

    /// <summary>
    /// Creates a category. With a parent it becomes a subcategory and inherits the
    /// parent's type; without one it is a new top-level group of <paramref name="type"/>.
    /// </summary>
    public async Task<Category> CreateAsync(string name, int? parentId, CategoryType type, CancellationToken ct = default)
    {
        var clean = ValidateName(name);
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        if (parentId is { } pid)
        {
            var parent = await RequireAsync(db, pid, ct);
            EnsureCanBeParent(parent);
            type = parent.CategoryType;
        }
        else if (!Enum.IsDefined(type))
        {
            throw new LedgerValidationException("Choose whether this is for spending, income or savings.");
        }

        await EnsureUniqueAmongSiblingsAsync(db, clean, parentId, type, exceptId: null, ct);

        var siblings = db.Categories.Where(c => c.ParentCategoryId == parentId && (parentId != null || c.CategoryType == type));
        var category = new Category
        {
            Name = clean,
            ParentCategoryId = parentId,
            CategoryType = type,
            SortOrder = await siblings.AnyAsync(ct) ? await siblings.MaxAsync(c => c.SortOrder, ct) + 1 : 0,
        };
        db.Categories.Add(category);
        await db.SaveChangesAsync(ct);
        return category;
    }

    public async Task RenameAsync(int id, string name, CancellationToken ct = default)
    {
        var clean = ValidateName(name);
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var category = await RequireAsync(db, id, ct);
        await EnsureUniqueAmongSiblingsAsync(db, clean, category.ParentCategoryId, category.CategoryType, exceptId: id, ct);
        category.Name = clean;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Moves a category under a different group of the same type, or to the top level
    /// when <paramref name="newParentId"/> is null. A group that has subcategories
    /// cannot itself become a subcategory.
    /// </summary>
    public async Task MoveAsync(int id, int? newParentId, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var category = await RequireAsync(db, id, ct);
        if (category.ParentCategoryId == newParentId)
        {
            return;
        }

        if (newParentId is { } pid)
        {
            if (pid == id)
            {
                throw new LedgerValidationException("A category can't be placed inside itself.");
            }
            var parent = await RequireAsync(db, pid, ct);
            EnsureCanBeParent(parent);
            if (parent.CategoryType != category.CategoryType)
            {
                throw new LedgerValidationException("A category can only be moved into a group of the same kind.");
            }
            if (await db.Categories.AnyAsync(c => c.ParentCategoryId == id, ct))
            {
                throw new LedgerValidationException("This category has its own subcategories, so it can't be moved into another group.");
            }
        }

        await EnsureUniqueAmongSiblingsAsync(db, category.Name, newParentId, category.CategoryType, exceptId: id, ct);

        var siblings = db.Categories.Where(c => c.ParentCategoryId == newParentId && c.Id != id
            && (newParentId != null || c.CategoryType == category.CategoryType));
        category.ParentCategoryId = newParentId;
        category.SortOrder = await siblings.AnyAsync(ct) ? await siblings.MaxAsync(c => c.SortOrder, ct) + 1 : 0;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Archives or restores a category. Archiving a group archives its subcategories;
    /// restoring a group restores them. Restoring a subcategory also restores its group,
    /// so an active subcategory never sits under an archived one.
    /// </summary>
    public async Task SetArchivedAsync(int id, bool archived, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var category = await RequireAsync(db, id, ct);
        category.IsArchived = archived;

        if (category.ParentCategoryId is null)
        {
            foreach (var child in await db.Categories.Where(c => c.ParentCategoryId == id).ToListAsync(ct))
            {
                child.IsArchived = archived;
            }
        }
        else if (!archived)
        {
            var parent = await RequireAsync(db, category.ParentCategoryId.Value, ct);
            parent.IsArchived = false;
        }

        await db.SaveChangesAsync(ct);
    }

    private static string ValidateName(string? name)
    {
        var clean = name?.Trim();
        if (string.IsNullOrEmpty(clean))
        {
            throw new LedgerValidationException("Give the category a name.");
        }
        if (clean.Length > MaxNameLength)
        {
            throw new LedgerValidationException($"Category names can be up to {MaxNameLength} characters.");
        }
        return clean;
    }

    private static void EnsureCanBeParent(Category parent)
    {
        if (parent.ParentCategoryId is not null)
        {
            throw new LedgerValidationException("Subcategories can't have their own subcategories.");
        }
        if (parent.IsArchived)
        {
            throw new LedgerValidationException("Restore that group before adding to it.");
        }
    }

    /// <summary>Names must be unique (ignoring case) among siblings, archived ones included.</summary>
    private static async Task EnsureUniqueAmongSiblingsAsync(
        LedgerDbContext db, string name, int? parentId, CategoryType type, int? exceptId, CancellationToken ct)
    {
        var siblingNames = await db.Categories
            .Where(c => c.ParentCategoryId == parentId && c.Id != exceptId)
            .Where(c => parentId != null || c.CategoryType == type)
            .Select(c => c.Name)
            .ToListAsync(ct);
        if (siblingNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new LedgerValidationException($"There's already a category called “{name}” here.");
        }
    }

    private static async Task<Category> RequireAsync(LedgerDbContext db, int id, CancellationToken ct) =>
        await db.Categories.FindAsync([id], ct)
        ?? throw new LedgerValidationException("That category no longer exists.");
}

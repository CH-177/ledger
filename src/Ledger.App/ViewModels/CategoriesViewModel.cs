using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ledger.App.Presentation;
using Ledger.Core;
using Ledger.Core.Models;
using Ledger.Core.Services;
using Microsoft.Extensions.Logging;

namespace Ledger.App.ViewModels;

/// <summary>One category as shown on the Categories page.</summary>
public sealed class CategoryRowViewModel(Category category, string? parentName, IReadOnlyList<CategoryRowViewModel> children)
{
    public Category Category { get; } = category;
    public int Id => Category.Id;
    public string Name => Category.Name;
    public bool IsGroup => Category.ParentCategoryId is null;
    public string Glyph => Category.Icon ?? CategoryDisplay.DefaultGlyph;
    public IReadOnlyList<CategoryRowViewModel> Children { get; } = children;

    public string Caption => Children.Count switch
    {
        0 => "No subcategories",
        1 => "1 subcategory",
        var n => $"{n} subcategories",
    };

    /// <summary>"Food › Groceries" for a subcategory, "Food" for a group.</summary>
    public string FullName => parentName is null ? Name : $"{parentName} › {Name}";

    public string MoreOptionsName => $"More options for {FullName}";

    public override string ToString() => FullName;
}

/// <summary>A "Spending" / "Income" / "Savings" section.</summary>
public sealed class CategorySectionViewModel(CategoryKindOption kind, IReadOnlyList<CategoryRowViewModel> groups)
{
    public CategoryType Type => kind.Type;
    public string Title => kind.Name;
    public string Description => kind.Description;
    public IReadOnlyList<CategoryRowViewModel> Groups { get; } = groups;
}

/// <summary>A destination offered when moving a category.</summary>
public sealed record MoveTarget(string Label, int? ParentId);

public sealed partial class CategoriesViewModel(CategoryService categories, ILogger<CategoriesViewModel> logger)
    : ObservableObject
{
    private IReadOnlyList<CategoryNode> _tree = [];

    public ObservableCollection<CategorySectionViewModel> Sections { get; } = [];
    public ObservableCollection<CategoryRowViewModel> Archived { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasArchived))]
    public partial bool IsLoaded { get; set; }

    public bool HasError => ErrorMessage is not null;
    public bool HasArchived => IsLoaded && Archived.Count > 0;

    [RelayCommand]
    public async Task LoadAsync()
    {
        ErrorMessage = null;
        try
        {
            _tree = await categories.GetTreeAsync(includeArchived: true);

            Sections.Clear();
            foreach (var kind in CategoryDisplay.Kinds)
            {
                var groups = _tree
                    .Where(n => n.Category.CategoryType == kind.Type && !n.Category.IsArchived)
                    .Select(n => new CategoryRowViewModel(n.Category, null,
                        n.Children.Where(c => !c.IsArchived).Select(c => new CategoryRowViewModel(c, n.Category.Name, [])).ToList()))
                    .ToList();
                Sections.Add(new CategorySectionViewModel(kind, groups));
            }

            Archived.Clear();
            foreach (var node in _tree)
            {
                if (node.Category.IsArchived)
                {
                    Archived.Add(new CategoryRowViewModel(node.Category, null, []));
                }
                // Subcategories archived on their own; ones archived along with their group come back with it.
                foreach (var child in node.Children.Where(c => c.IsArchived && !node.Category.IsArchived))
                {
                    Archived.Add(new CategoryRowViewModel(child, node.Category.Name, []));
                }
            }

            IsLoaded = false;
            IsLoaded = true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load categories");
            ErrorMessage = "Something went wrong while loading your categories. Your existing data has not been changed.";
        }
    }

    /// <summary>Where a category could be moved: other groups of the same kind, or out to its own group.</summary>
    public IReadOnlyList<MoveTarget> GetMoveTargets(CategoryRowViewModel row)
    {
        if (row.IsGroup && _tree.Any(n => n.Category.Id == row.Id && n.Children.Count > 0))
        {
            return []; // groups with subcategories stay at the top level
        }

        var targets = _tree
            .Where(n => n.Category.CategoryType == row.Category.CategoryType && !n.Category.IsArchived)
            .Where(n => n.Category.Id != row.Id && n.Category.Id != row.Category.ParentCategoryId)
            .Select(n => new MoveTarget(n.Category.Name, n.Category.Id))
            .ToList();
        if (!row.IsGroup)
        {
            targets.Insert(0, new MoveTarget("Make it a separate group", null));
        }
        return targets;
    }

    public Task MoveAsync(CategoryRowViewModel row, MoveTarget target) =>
        RunAsync(() => categories.MoveAsync(row.Id, target.ParentId), "moving this category");

    public Task SetArchivedAsync(CategoryRowViewModel row, bool archived) =>
        RunAsync(() => categories.SetArchivedAsync(row.Id, archived), "updating this category");

    private async Task RunAsync(Func<Task> action, string activity)
    {
        ErrorMessage = null;
        try
        {
            await action();
        }
        catch (LedgerValidationException ex)
        {
            ErrorMessage = ex.Message;
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Category operation failed");
            ErrorMessage = $"Something went wrong while {activity}. Your existing data has not been changed.";
            return;
        }
        await LoadAsync();
    }
}

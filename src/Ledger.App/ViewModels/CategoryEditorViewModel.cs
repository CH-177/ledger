using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Ledger.App.Presentation;
using Ledger.Core;
using Ledger.Core.Services;
using Microsoft.Extensions.Logging;

namespace Ledger.App.ViewModels;

/// <summary>A choice in the "Group" box: an existing group, or none (a new top-level group).</summary>
public sealed record GroupOption(string Name, int? Id)
{
    public override string ToString() => Name;
}

/// <summary>Form state for adding a category or renaming one.</summary>
public sealed partial class CategoryEditorViewModel(CategoryService categories, ILogger<CategoryEditorViewModel> logger)
    : ObservableObject
{
    private const string NoGroupLabel = "None: make it a new group";

    private IReadOnlyList<CategoryNode> _tree = [];
    private int? _renamingId;

    public IReadOnlyList<CategoryKindOption> Kinds => CategoryDisplay.Kinds;

    public ObservableCollection<GroupOption> GroupOptions { get; } = [];

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KindDescription))]
    public partial int KindIndex { get; set; }

    [ObservableProperty]
    public partial int GroupIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial bool IsSaving { get; set; }

    public bool HasError => ErrorMessage is not null;
    public bool IsRename => _renamingId is not null;
    public bool IsCreate => !IsRename;
    public string Title => IsRename ? "Rename category" : "Add category";
    public string KindDescription => Kinds[Math.Clamp(KindIndex, 0, Kinds.Count - 1)].Description;

    /// <summary>Prepares to add a category, optionally preselecting the group it goes in.</summary>
    public async Task LoadForCreateAsync(int? groupId = null)
    {
        _renamingId = null;
        _tree = await categories.GetTreeAsync();
        Name = "";
        ErrorMessage = null;

        var group = _tree.FirstOrDefault(n => n.Category.Id == groupId)?.Category;
        KindIndex = CategoryDisplay.IndexOf(group?.CategoryType ?? Ledger.Core.Models.CategoryType.Expense);
        RefreshGroups();
        GroupIndex = Math.Max(0, GroupOptions.ToList().FindIndex(o => o.Id == groupId));
    }

    public void LoadForRename(CategoryRowViewModel row)
    {
        _renamingId = row.Id;
        Name = row.Name;
        ErrorMessage = null;
    }

    partial void OnKindIndexChanged(int value)
    {
        if (IsCreate)
        {
            RefreshGroups();
            GroupIndex = 0;
        }
    }

    private void RefreshGroups()
    {
        var kind = Kinds[Math.Clamp(KindIndex, 0, Kinds.Count - 1)].Type;
        GroupOptions.Clear();
        GroupOptions.Add(new GroupOption(NoGroupLabel, null));
        foreach (var node in _tree.Where(n => n.Category.CategoryType == kind))
        {
            GroupOptions.Add(new GroupOption(node.Category.Name, node.Category.Id));
        }
    }

    /// <summary>Validates and saves. Returns false and sets <see cref="ErrorMessage"/> on failure.</summary>
    public async Task<bool> SaveAsync()
    {
        ErrorMessage = null;
        if (string.IsNullOrWhiteSpace(Name))
        {
            ErrorMessage = "Give the category a name.";
            return false;
        }

        IsSaving = true;
        try
        {
            if (_renamingId is { } id)
            {
                await categories.RenameAsync(id, Name);
            }
            else
            {
                var parentId = GroupIndex >= 0 && GroupIndex < GroupOptions.Count ? GroupOptions[GroupIndex].Id : null;
                await categories.CreateAsync(Name, parentId, Kinds[Math.Clamp(KindIndex, 0, Kinds.Count - 1)].Type);
            }
            return true;
        }
        catch (LedgerValidationException ex)
        {
            ErrorMessage = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save category");
            ErrorMessage = "Something went wrong while saving this category. Your existing data has not been changed.";
            return false;
        }
        finally
        {
            IsSaving = false;
        }
    }
}

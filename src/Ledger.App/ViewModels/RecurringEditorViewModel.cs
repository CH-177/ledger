using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Ledger.App.Presentation;
using Ledger.Core;
using Ledger.Core.Forecasting;
using Ledger.Core.Models;
using Ledger.Core.Services;
using Microsoft.Extensions.Logging;

namespace Ledger.App.ViewModels;

/// <summary>Form state for adding or editing a bill, subscription, recurring income or transfer.</summary>
public sealed partial class RecurringEditorViewModel(
    RecurringService recurring,
    AccountService accounts,
    CategoryService categories,
    TimeProvider time,
    ILogger<RecurringEditorViewModel> logger) : ObservableObject
{
    private static readonly TransactionType[] TypeOrder = [TransactionType.Expense, TransactionType.Income, TransactionType.Transfer];

    private int? _editingId;
    private RecurringTransaction? _original;
    private IReadOnlyList<CategoryNode> _tree = [];

    public IReadOnlyList<string> FrequencyOptions { get; } = RecurrenceDisplay.Options.Select(o => o.Label).ToList();
    public ObservableCollection<FilterOption<int?>> AccountOptions { get; } = [];
    public ObservableCollection<FilterOption<int?>> ToAccountOptions { get; } = [];
    public ObservableCollection<FilterOption<int?>> CategoryOptions { get; } = [];

    /// <summary>0 = Bill, 1 = Income, 2 = Transfer.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTransfer), nameof(AccountLabel), nameof(CategoryLabel), nameof(NamePlaceholder), nameof(EstimateText))]
    public partial int TypeIndex { get; set; }

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string AmountText { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanAutoRecord))]
    public partial bool IsAmountEstimated { get; set; }

    /// <summary>Record each due date as a real transaction automatically (fixed amounts only).</summary>
    [ObservableProperty] public partial bool AutoRecord { get; set; }

    public bool CanAutoRecord => !IsAmountEstimated;

    partial void OnIsAmountEstimatedChanged(bool value)
    {
        if (value)
        {
            AutoRecord = false;
        }
    }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(SchedulePreview))]
    public partial int FrequencyIndex { get; set; } = 2;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(SchedulePreview))]
    public partial DateTimeOffset? StartDate { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(SchedulePreview))]
    public partial bool HasEndDate { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(SchedulePreview))]
    public partial DateTimeOffset? EndDate { get; set; }

    [ObservableProperty] public partial int AccountIndex { get; set; }
    [ObservableProperty] public partial int ToAccountIndex { get; set; }
    [ObservableProperty] public partial int CategoryIndex { get; set; }
    [ObservableProperty] public partial string Memo { get; set; } = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty] public partial bool IsSaving { get; set; }

    public bool HasError => ErrorMessage is not null;
    public string Title => _editingId is null ? "Add recurring" : $"Edit {_original?.Name}";
    private TransactionType SelectedType => TypeOrder[Math.Clamp(TypeIndex, 0, TypeOrder.Length - 1)];
    private RecurrenceFrequency SelectedFrequency => RecurrenceDisplay.Options[Math.Clamp(FrequencyIndex, 0, RecurrenceDisplay.Options.Count - 1)].Value;
    public bool IsTransfer => SelectedType == TransactionType.Transfer;
    public string AccountLabel => IsTransfer ? "From account" : SelectedType == TransactionType.Income ? "Paid into" : "Paid from";
    public string CategoryLabel => IsTransfer ? "Savings category (optional)" : "Category (optional)";
    public string NamePlaceholder => SelectedType switch
    {
        TransactionType.Income => "e.g. Photography",
        TransactionType.Transfer => "e.g. Credit card payment",
        _ => "e.g. Spotify",
    };
    public string EstimateText => SelectedType == TransactionType.Income
        ? "The amount varies (this is an estimate)"
        : "The amount varies (this is an estimate, like a utility bill)";

    /// <summary>"Monthly on the 15th. Next: Wed, Oct 15, then Nov 15, Dec 15."</summary>
    public string SchedulePreview
    {
        get
        {
            if (StartDate is not { } start)
            {
                return "";
            }
            var first = DateOnly.FromDateTime(start.Date);
            DateOnly? end = HasEndDate && EndDate is { } e ? DateOnly.FromDateTime(e.Date) : null;
            var today = DateOnly.FromDateTime(time.GetLocalNow().Date);
            var next = RecurrenceSchedule.Between(first, end, SelectedFrequency, today, today.AddYears(3)).Take(3).ToList();
            var describe = RecurrenceDisplay.Describe(SelectedFrequency, first) + ".";
            return next.Count switch
            {
                0 => $"{describe} No dates left from today.",
                1 => $"{describe} Next: {RecurrenceDisplay.ShortDate(next[0])}.",
                _ => $"{describe} Next: {RecurrenceDisplay.ShortDate(next[0])}, then {string.Join(", ", next.Skip(1).Select(d => d.ToString("MMM d", System.Globalization.CultureInfo.GetCultureInfo("en-US"))))}.",
            };
        }
    }

    public async Task LoadForCreateAsync()
    {
        _editingId = null;
        _original = null;
        await LoadListsAsync();
        TypeIndex = 0;
        RefreshCategories();
        FrequencyIndex = 2;
        StartDate = time.GetLocalNow().Date;
        AccountIndex = 0;
        ToAccountIndex = 0;
        CategoryIndex = 0;
    }

    /// <summary>A new bill filled in from a repeat Ledger spotted, starting on its next expected date.</summary>
    public async Task LoadFromRepeatAsync(RepeatSuggestion repeat)
    {
        await LoadForCreateAsync();
        TypeIndex = Math.Max(0, Array.IndexOf(TypeOrder, repeat.Type));
        RefreshCategories();
        Name = repeat.Payee;
        AmountText = Money.FormatForEditing(repeat.TypicalCents);
        IsAmountEstimated = repeat.AmountVaries;
        FrequencyIndex = Math.Max(0, RecurrenceDisplay.Options.ToList().FindIndex(o => o.Value == repeat.Frequency));
        StartDate = new DateTimeOffset(repeat.NextDate.ToDateTime(TimeOnly.MinValue));
        AccountIndex = Math.Max(0, AccountOptions.ToList().FindIndex(o => o.Value == repeat.AccountId));
        CategoryIndex = Math.Max(0, CategoryOptions.ToList().FindIndex(o => o.Value == repeat.CategoryId));
    }

    public async Task LoadForEditAsync(int id)
    {
        var rule = await recurring.GetAsync(id);
        _editingId = id;
        _original = rule;
        await LoadListsAsync();

        TypeIndex = Array.IndexOf(TypeOrder, rule.TransactionType);
        RefreshCategories();
        Name = rule.Name;
        AmountText = Money.FormatForEditing(rule.AmountCents);
        IsAmountEstimated = rule.IsAmountEstimated;
        AutoRecord = rule.AutoRecordSince is not null;
        FrequencyIndex = RecurrenceDisplay.Options.ToList().FindIndex(o => o.Value == rule.Frequency);
        StartDate = new DateTimeOffset(rule.StartDate.ToDateTime(TimeOnly.MinValue));
        HasEndDate = rule.EndDate is not null;
        EndDate = rule.EndDate is { } end ? new DateTimeOffset(end.ToDateTime(TimeOnly.MinValue)) : null;
        AccountIndex = Math.Max(0, AccountOptions.ToList().FindIndex(o => o.Value == rule.AccountId));
        ToAccountIndex = Math.Max(0, ToAccountOptions.ToList().FindIndex(o => o.Value == rule.TransferAccountId));
        CategoryIndex = Math.Max(0, CategoryOptions.ToList().FindIndex(o => o.Value == rule.CategoryId));
        Memo = rule.Memo ?? "";
    }

    private async Task LoadListsAsync()
    {
        var all = (await accounts.GetAccountBalancesAsync(includeArchived: true)).Select(b => b.Account).ToList();
        _tree = await categories.GetTreeAsync(includeArchived: true);

        var usable = all.Where(a => !a.IsArchived || a.Id == _original?.AccountId || a.Id == _original?.TransferAccountId).ToList();
        AccountOptions.Clear();
        ToAccountOptions.Clear();
        ToAccountOptions.Add(new("Choose an account", null));
        foreach (var a in usable)
        {
            AccountOptions.Add(new(a.Name, a.Id));
            ToAccountOptions.Add(new(a.Name, a.Id));
        }
    }

    partial void OnTypeIndexChanged(int value)
    {
        var previous = CategoryOptions.ElementAtOrDefault(CategoryIndex)?.Value;
        RefreshCategories();
        CategoryIndex = Math.Max(0, CategoryOptions.ToList().FindIndex(o => o.Value == previous));
    }

    private void RefreshCategories()
    {
        var kind = SelectedType switch
        {
            TransactionType.Income => CategoryType.Income,
            TransactionType.Transfer => CategoryType.Savings,
            _ => CategoryType.Expense,
        };
        CategoryOptions.Clear();
        CategoryOptions.Add(new("No category", null));
        foreach (var node in _tree.Where(n => n.Category.CategoryType == kind))
        {
            if (Usable(node.Category))
            {
                CategoryOptions.Add(new(node.Category.Name, node.Category.Id));
            }
            foreach (var child in node.Children.Where(Usable))
            {
                CategoryOptions.Add(new($"{node.Category.Name} › {child.Name}", child.Id));
            }
        }

        bool Usable(Category c) => !c.IsArchived || c.Id == _original?.CategoryId;
    }

    public async Task<bool> SaveAsync()
    {
        ErrorMessage = null;
        if (string.IsNullOrWhiteSpace(Name))
        {
            ErrorMessage = "Give it a name, like “Spotify” or “Car insurance”.";
            return false;
        }
        if (!Money.TryParse(AmountText, out var cents) || cents <= 0)
        {
            ErrorMessage = IsAmountEstimated
                ? "Enter your best estimate of the amount, like 45.00."
                : "Enter an amount greater than zero, like 11.99.";
            return false;
        }
        if (StartDate is not { } start)
        {
            ErrorMessage = "Choose the first (or next) date it happens.";
            return false;
        }
        if (HasEndDate && EndDate is null)
        {
            ErrorMessage = "Choose the last date, or untick “Stops on a date”.";
            return false;
        }
        if (AccountOptions.ElementAtOrDefault(AccountIndex)?.Value is not { } accountId)
        {
            ErrorMessage = "Add an account first, on the Accounts page.";
            return false;
        }

        var input = new RecurringInput(
            Name,
            SelectedType,
            cents,
            SelectedFrequency,
            DateOnly.FromDateTime(start.Date),
            accountId,
            HasEndDate && EndDate is { } end ? DateOnly.FromDateTime(end.Date) : null,
            IsTransfer ? ToAccountOptions.ElementAtOrDefault(ToAccountIndex)?.Value : null,
            CategoryOptions.ElementAtOrDefault(CategoryIndex)?.Value,
            IsAmountEstimated,
            Memo,
            AutoRecord && !IsAmountEstimated);

        IsSaving = true;
        try
        {
            if (_editingId is { } id)
            {
                await recurring.UpdateAsync(id, input);
            }
            else
            {
                await recurring.CreateAsync(input);
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
            logger.LogError(ex, "Failed to save recurring item");
            ErrorMessage = "Something went wrong while saving. Your existing data has not been changed.";
            return false;
        }
        finally
        {
            IsSaving = false;
        }
    }
}

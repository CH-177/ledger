using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Ledger.Core;
using Ledger.Core.Forecasting;
using Ledger.Core.Models;
using Ledger.Core.Services;
using Microsoft.Extensions.Logging;

namespace Ledger.App.ViewModels;

/// <summary>Form state for adding or editing a savings goal.</summary>
public sealed partial class GoalEditorViewModel(
    GoalService goals,
    AccountService accounts,
    CategoryService categories,
    TimeProvider time,
    ILogger<GoalEditorViewModel> logger) : ObservableObject
{
    private int? _editingId;
    private Goal? _original;
    private Dictionary<int, long> _balances = [];

    /// <summary>For manual goals: optional "kept in". Index 0 is "Not set".</summary>
    public ObservableCollection<FilterOption<int?>> AccountOptions { get; } = [];

    /// <summary>Spending categories a sinking fund can pay for. Index 0 is "None".</summary>
    public ObservableCollection<FilterOption<int?>> CategoryOptions { get; } = [];

    /// <summary>Money set aside for a cost you know is coming, rather than saving toward something.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsNotFund), nameof(Title), nameof(IsManual), nameof(IsAccountBacked), nameof(AccountLabel))]
    public partial bool IsSinkingFund { get; set; }

    [ObservableProperty] public partial string MonthlyText { get; set; } = "";
    [ObservableProperty] public partial int CategoryIndex { get; set; }
    public bool IsNotFund => !IsSinkingFund;

    partial void OnIsSinkingFundChanged(bool value)
    {
        if (value) TrackingIndex = 0; // a fund keeps its own set-aside
    }

    [ObservableProperty] public partial string Name { get; set; } = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Preview))]
    public partial string TargetText { get; set; } = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Preview))]
    public partial bool HasTargetDate { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Preview))]
    public partial DateTimeOffset? TargetDate { get; set; }

    /// <summary>0 = I'll add money as I save, 1 = use an account's balance.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsManual), nameof(IsAccountBacked), nameof(AccountLabel), nameof(Preview))]
    public partial int TrackingIndex { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Preview))]
    public partial string StartingText { get; set; } = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Preview))]
    public partial int AccountIndex { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty] public partial bool IsSaving { get; set; }

    public bool HasError => ErrorMessage is not null;
    public string Title => _editingId is null ? (IsSinkingFund ? "Add sinking fund" : "Add goal") : $"Edit {_original?.Name}";
    public bool IsManual => TrackingIndex == 0;
    public bool IsAccountBacked => TrackingIndex == 1;
    public string AccountLabel => IsManual ? "Where the money is kept (optional)" : "Account";

    /// <summary>"$380.00 to go. About $42.23 a month from now until June 2027."</summary>
    public string Preview
    {
        get
        {
            if (!Money.TryParse(TargetText, out var target) || target <= 0)
            {
                return "";
            }
            long current;
            if (IsManual)
            {
                current = Money.TryParse(StartingText, out var s) && s > 0 ? s : 0;
                current += _original is { Tracking: GoalTracking.Manual } ? _contributed : 0;
            }
            else
            {
                current = AccountOptions.ElementAtOrDefault(AccountIndex)?.Value is { } id ? Math.Max(0, _balances.GetValueOrDefault(id)) : 0;
            }

            var remaining = GoalMath.Remaining(target, current);
            if (remaining == 0)
            {
                return "Already reached.";
            }
            var today = DateOnly.FromDateTime(time.GetLocalNow().Date);
            DateOnly? date = HasTargetDate && TargetDate is { } d ? DateOnly.FromDateTime(d.Date) : null;
            var monthly = GoalMath.SuggestedMonthly(target, current, today, date);
            return monthly is { } m
                ? $"{Money.Format(remaining)} to go: about {Money.Format(m)} a month, starting now. A suggestion only; nothing moves automatically."
                : date is not null ? $"{Money.Format(remaining)} to go. The target date has passed." : $"{Money.Format(remaining)} to go.";
        }
    }

    private long _contributed;

    public async Task LoadForCreateAsync()
    {
        _editingId = null;
        _original = null;
        _contributed = 0;
        await LoadAccountsAsync();
        await LoadCategoriesAsync();
        TrackingIndex = 0;
        AccountIndex = 0;
        IsSinkingFund = false;
        MonthlyText = "";
        CategoryIndex = 0;
    }

    /// <summary>A new sinking fund (from "Add sinking fund").</summary>
    public async Task LoadForNewFundAsync()
    {
        await LoadForCreateAsync();
        IsSinkingFund = true;
    }

    private async Task LoadCategoriesAsync()
    {
        CategoryOptions.Clear();
        CategoryOptions.Add(new("None", null));
        foreach (var node in (await categories.GetTreeAsync()).Where(n => n.Category.CategoryType == CategoryType.Expense))
        {
            CategoryOptions.Add(new(node.Category.Name, node.Category.Id));
            foreach (var child in node.Children)
            {
                CategoryOptions.Add(new($"{node.Category.Name} › {child.Name}", child.Id));
            }
        }
    }

    public async Task LoadForEditAsync(int id)
    {
        var progress = await goals.GetAsync(id);
        var goal = progress.Goal;
        _editingId = id;
        _original = goal;
        _contributed = goal.Tracking == GoalTracking.Manual ? progress.CurrentCents - goal.StartingAmountCents : 0;
        await LoadAccountsAsync();
        await LoadCategoriesAsync();
        IsSinkingFund = goal.IsSinkingFund;
        MonthlyText = goal.MonthlySetAsideCents is { } monthly ? Money.FormatForEditing(monthly) : "";
        CategoryIndex = Math.Max(0, CategoryOptions.ToList().FindIndex(o => o.Value == goal.CategoryId));

        Name = goal.Name;
        TargetText = Money.FormatForEditing(goal.TargetAmountCents);
        HasTargetDate = goal.TargetDate is not null;
        TargetDate = goal.TargetDate is { } d ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue)) : null;
        TrackingIndex = goal.Tracking == GoalTracking.AccountBalance ? 1 : 0;
        StartingText = goal.StartingAmountCents > 0 ? Money.FormatForEditing(goal.StartingAmountCents) : "";
        AccountIndex = Math.Max(0, AccountOptions.ToList().FindIndex(o => o.Value == goal.AccountId));
    }

    private async Task LoadAccountsAsync()
    {
        var all = await accounts.GetAccountBalancesAsync(includeArchived: true);
        _balances = all.ToDictionary(b => b.Account.Id, b => b.BalanceCents);
        AccountOptions.Clear();
        AccountOptions.Add(new("Not set", null));
        foreach (var b in all.Where(b => !b.Account.IsLiability && (!b.Account.IsArchived || b.Account.Id == _original?.AccountId)))
        {
            AccountOptions.Add(new($"{b.Account.Name}  ({Money.Format(b.BalanceCents)})", b.Account.Id));
        }
    }

    public async Task<bool> SaveAsync()
    {
        ErrorMessage = null;
        if (string.IsNullOrWhiteSpace(Name))
        {
            ErrorMessage = "Give the goal a name, like “Emergency fund”.";
            return false;
        }
        if (!Money.TryParse(TargetText, out var target) || target <= 0)
        {
            ErrorMessage = "Enter a target greater than zero, like 1200.";
            return false;
        }
        long starting = 0;
        if (IsManual && !string.IsNullOrWhiteSpace(StartingText) && (!Money.TryParse(StartingText, out starting) || starting < 0))
        {
            ErrorMessage = "Enter the amount already saved, like 820, or leave it blank.";
            return false;
        }
        if (HasTargetDate && TargetDate is null)
        {
            ErrorMessage = "Choose a target date, or untick “Has a target date”.";
            return false;
        }
        var accountId = AccountOptions.ElementAtOrDefault(AccountIndex)?.Value;
        long monthly = 0;
        if (IsSinkingFund && !string.IsNullOrWhiteSpace(MonthlyText) && (!Money.TryParse(MonthlyText, out monthly) || monthly < 0))
        {
            ErrorMessage = "Enter the amount to set aside each month, like 50, or leave it blank.";
            return false;
        }

        var input = new GoalInput(
            Name,
            target,
            HasTargetDate && TargetDate is { } d ? DateOnly.FromDateTime(d.Date) : null,
            IsManual ? GoalTracking.Manual : GoalTracking.AccountBalance,
            starting,
            accountId,
            IsSinkingFund,
            monthly > 0 ? monthly : null,
            IsSinkingFund ? CategoryOptions.ElementAtOrDefault(CategoryIndex)?.Value : null);

        IsSaving = true;
        try
        {
            if (_editingId is { } id)
            {
                await goals.UpdateAsync(id, input);
            }
            else
            {
                await goals.CreateAsync(input);
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
            logger.LogError(ex, "Failed to save goal");
            ErrorMessage = "Something went wrong while saving. Your existing data has not been changed.";
            return false;
        }
        finally
        {
            IsSaving = false;
        }
    }
}

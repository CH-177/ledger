using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ledger.App.Presentation;
using Ledger.Core;
using Ledger.Core.Services;
using Microsoft.Extensions.Logging;

namespace Ledger.App.ViewModels;

/// <summary>One spending group's row. Updated in place so editing keeps keyboard focus.</summary>
public sealed partial class BudgetLineViewModel : ObservableObject
{
    public BudgetLineViewModel(BudgetLine line) => Update(line);

    public int? CategoryId { get; private set; }

    [ObservableProperty] public partial string Name { get; private set; } = "";
    [ObservableProperty] public partial string Glyph { get; private set; } = "";
    [ObservableProperty] public partial string PlannedText { get; set; } = "";
    [ObservableProperty] public partial string AmountsText { get; private set; } = "";
    [ObservableProperty] public partial string StatusText { get; private set; } = "";
    [ObservableProperty] public partial double Percent { get; private set; }
    [ObservableProperty] public partial bool IsEditable { get; private set; }
    [ObservableProperty] public partial bool IsOver { get; private set; }
    [ObservableProperty] public partial bool IsNear { get; private set; }
    [ObservableProperty] public partial bool IsOnTrack { get; private set; }
    [ObservableProperty] public partial bool IsNoPlan { get; private set; }

    public string PlanFieldName => $"Planned amount for {Name}";

    public void Update(BudgetLine line)
    {
        CategoryId = line.CategoryId;
        Name = line.IsArchived ? $"{line.Name} (archived)" : line.Name;
        Glyph = line.IsUncategorized ? "" : line.Icon ?? CategoryDisplay.DefaultGlyph;
        PlannedText = line.PlannedCents == 0 ? "" : Money.FormatForEditing(line.PlannedCents);
        IsEditable = !line.IsUncategorized && !line.IsArchived;
        Percent = line.FractionUsed * 100;

        IsOver = line.Status == BudgetLineStatus.Over;
        IsNear = line.Status == BudgetLineStatus.NearLimit;
        IsOnTrack = line.Status == BudgetLineStatus.OnTrack;
        IsNoPlan = line.Status == BudgetLineStatus.NoPlan;

        AmountsText = line.ActualCents < 0
            ? $"{Money.FormatCompact(line.ActualCents)} (refunds)" + (line.PlannedCents > 0 ? $" of {Money.FormatCompact(line.PlannedCents)}" : "")
            : line.Status == BudgetLineStatus.NoPlan
            ? $"{Money.FormatCompact(line.ActualCents)} spent"
            : $"{Money.FormatCompact(line.ActualCents)} of {Money.FormatCompact(line.PlannedCents)}";
        StatusText = line.Status switch
        {
            BudgetLineStatus.Over => $"{Money.FormatCompact(-line.RemainingCents)} over",
            BudgetLineStatus.NoPlan when line.IsUncategorized => "Can't be planned. Add categories to these transactions.",
            BudgetLineStatus.NoPlan => line.ActualCents > 0 ? "Not planned" : line.ActualCents < 0 ? "More came back than was spent" : "",
            BudgetLineStatus.NearLimit => $"{Money.FormatCompact(line.RemainingCents)} left, nearly used",
            _ => $"{Money.FormatCompact(line.RemainingCents)} left",
        };
        OnPropertyChanged(nameof(PlanFieldName));
    }
}

/// <summary>A sinking fund on the Budget page: its monthly amount and whether this month's is set aside.</summary>
public sealed class FundRowViewModel(GoalProgress fund, long setAsideThisMonth, bool isCurrentMonth)
{
    public GoalProgress Fund { get; } = fund;
    public string Name => Fund.Goal.Name;
    public long Monthly => Fund.Goal.MonthlySetAsideCents ?? 0;
    public string DetailText =>
        (Monthly > 0 ? $"{Money.Format(setAsideThisMonth)} of {Money.Format(Monthly)} set aside this month" : $"{Money.Format(setAsideThisMonth)} set aside this month") +
        $"  ·  {Money.Format(Fund.CurrentCents)} in the fund";
    public bool CanSetAside => isCurrentMonth && Monthly > 0 && setAsideThisMonth < Monthly;
    public string SetAsideText => $"Set aside {Money.Format(Monthly)}";
    public string SetAsideName => $"Set aside {Money.Format(Monthly)} for {Name}";
}

public sealed partial class BudgetViewModel(BudgetService budgets, GoalService goals, TimeProvider time, ILogger<BudgetViewModel> logger)
    : ObservableObject
{
    /// <summary>Sinking funds: set-asides are earmarks, not spending, so they sit beside the plan rather than in it.</summary>
    public ObservableCollection<FundRowViewModel> Funds { get; } = [];
    [ObservableProperty] public partial bool HasFunds { get; set; }
    [ObservableProperty] public partial string FundsSummary { get; set; } = "";

    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");

    public ObservableCollection<BudgetLineViewModel> Lines { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MonthTitle), nameof(SummaryTitle), nameof(IsCurrentMonth), nameof(IsNotCurrentMonth))]
    public partial DateOnly MonthStart { get; set; }

    [ObservableProperty] public partial string PlannedText { get; set; } = "";
    [ObservableProperty] public partial string SpentText { get; set; } = "";
    [ObservableProperty] public partial string RemainingText { get; set; } = "";
    [ObservableProperty] public partial string RemainingLabel { get; set; } = "REMAINING";
    [ObservableProperty] public partial double Percent { get; set; }
    [ObservableProperty] public partial bool IsOver { get; set; }
    [ObservableProperty] public partial bool IsNotOver { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoPlanPrompt))]
    public partial bool HasPlan { get; set; }

    [ObservableProperty] public partial bool CanCopyPreviousPlan { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoPlanPrompt), nameof(ShowNoGroups))]
    public partial bool IsLoaded { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => ErrorMessage is not null;
    public string MonthTitle => MonthStart.ToString("MMMM yyyy", Culture);
    public string SummaryTitle => $"{MonthStart.ToString("MMMM", Culture)} budget";
    public bool IsCurrentMonth => MonthStart == CurrentMonthStart;
    public bool IsNotCurrentMonth => !IsCurrentMonth;
    public bool ShowNoPlanPrompt => IsLoaded && !HasPlan;
    public bool ShowNoGroups => IsLoaded && Lines.Count == 0;

    private DateOnly CurrentMonthStart
    {
        get
        {
            var now = time.GetLocalNow();
            return new DateOnly(now.Year, now.Month, 1);
        }
    }

    public Task InitializeAsync()
    {
        MonthStart = CurrentMonthStart;
        return LoadAsync();
    }

    [RelayCommand]
    private Task PreviousMonthAsync() => GoToAsync(MonthStart.AddMonths(-1));

    [RelayCommand]
    private Task NextMonthAsync() => GoToAsync(MonthStart.AddMonths(1));

    [RelayCommand]
    private Task ThisMonthAsync() => GoToAsync(CurrentMonthStart);

    private Task GoToAsync(DateOnly monthStart)
    {
        MonthStart = monthStart;
        Lines.Clear();
        return LoadAsync();
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        ErrorMessage = null;
        try
        {
            var month = await budgets.GetMonthAsync(MonthStart.Year, MonthStart.Month);
            Apply(month);
            await LoadFundsAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load budget");
            ErrorMessage = "Something went wrong while loading this budget. Your existing data has not been changed.";
        }
    }

    private async Task LoadFundsAsync()
    {
        Funds.Clear();
        var funds = await goals.GetFundsAsync();
        var setAside = await goals.GetSetAsideInMonthAsync(MonthStart.Year, MonthStart.Month);
        foreach (var f in funds)
        {
            Funds.Add(new FundRowViewModel(f, setAside.GetValueOrDefault(f.Goal.Id), IsCurrentMonth));
        }
        HasFunds = Funds.Count > 0;
        var planned = funds.Sum(f => f.Goal.MonthlySetAsideCents ?? 0);
        FundsSummary = $"{Money.Format(setAside.Values.Sum())} of {Money.Format(planned)} set aside for {MonthTitle}. Setting money aside doesn't move it between accounts; it's kept out of 'Free to spend'.";
    }

    public async Task SetAsideAsync(FundRowViewModel row)
    {
        ErrorMessage = null;
        try
        {
            await goals.SetAsideMonthlyAsync(row.Fund.Goal.Id);
            await LoadFundsAsync();
        }
        catch (LedgerValidationException ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task CopyPreviousPlanAsync()
    {
        ErrorMessage = null;
        try
        {
            await budgets.CopyPreviousPlanAsync(MonthStart.Year, MonthStart.Month);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to copy budget");
            ErrorMessage = "Something went wrong while copying last month's plan. Your existing data has not been changed.";
            return;
        }
        await LoadAsync();
    }

    /// <summary>Saves a row's planned amount from its text box. Blank means no plan.</summary>
    public async Task CommitPlanAsync(BudgetLineViewModel line)
    {
        if (line.CategoryId is not { } categoryId || !line.IsEditable)
        {
            return;
        }
        string? error = null;
        long cents = 0;
        var isBlank = string.IsNullOrWhiteSpace(line.PlannedText);
        if (!isBlank && !Money.TryParse(line.PlannedText, out cents))
        {
            error = $"Enter the plan for {line.Name} as an amount, like 250.";
        }
        else
        {
            try
            {
                await budgets.SetPlannedAsync(MonthStart.Year, MonthStart.Month, categoryId, cents);
            }
            catch (LedgerValidationException ex)
            {
                error = ex.Message;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to save budget plan");
                error = "Something went wrong while saving this plan. Your existing data has not been changed.";
            }
        }

        // Reload first (it clears old messages and restores the saved value), then report.
        await LoadAsync();
        ErrorMessage ??= error;
    }

    private void Apply(BudgetMonth month)
    {
        // Update rows in place when the set of groups is unchanged, so focus survives a save.
        var sameRows = Lines.Count == month.Lines.Count && Lines.Select(l => l.CategoryId).SequenceEqual(month.Lines.Select(l => l.CategoryId));
        if (sameRows)
        {
            for (var i = 0; i < Lines.Count; i++)
            {
                Lines[i].Update(month.Lines[i]);
            }
        }
        else
        {
            Lines.Clear();
            foreach (var line in month.Lines)
            {
                Lines.Add(new BudgetLineViewModel(line));
            }
        }

        PlannedText = Money.FormatCompact(month.PlannedCents);
        SpentText = Money.FormatCompact(month.ActualCents);
        IsOver = month.RemainingCents < 0;
        IsNotOver = !IsOver;
        RemainingText = Money.FormatCompact(Math.Abs(month.RemainingCents));
        RemainingLabel = IsOver ? "OVER BY" : "REMAINING";
        Percent = month.FractionUsed * 100;
        HasPlan = month.HasPlan;
        CanCopyPreviousPlan = month.CanCopyPreviousPlan;
        IsLoaded = false;
        IsLoaded = true;
    }
}

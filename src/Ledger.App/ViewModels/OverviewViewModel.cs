using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ledger.App.Presentation;
using Ledger.App.Services;
using Ledger.Core;
using Ledger.Core.Services;
using Microsoft.Extensions.Logging;

namespace Ledger.App.ViewModels;

/// <summary>One line of "This month": label and amount.</summary>
public sealed record MonthLineViewModel(string Label, string AmountText, bool IsTotal);

/// <summary>A budget group in brief: "Food  $187.00 of $250.00" with a bar and a status in words.</summary>
public sealed class BudgetBriefViewModel(BudgetLine line)
{
    public string Name => line.Name;
    public string AmountText => $"{Money.Format(line.ActualCents)} of {Money.Format(line.PlannedCents)}";
    public double Percent => line.FractionUsed * 100;
    public string StatusText => line.Status switch
    {
        BudgetLineStatus.Over => $"Over by {Money.Format(-line.RemainingCents)}",
        BudgetLineStatus.NearLimit => $"{Money.Format(line.RemainingCents)} left, nearly used",
        _ => $"{Money.Format(line.RemainingCents)} left",
    };
    public bool IsOver => line.Status == BudgetLineStatus.Over;
    public bool IsNotOver => !IsOver;
    public override string ToString() => $"{Name}, {AmountText}, {StatusText}";
}

/// <summary>
/// The dashboard. It only arranges what the account, dashboard, forecast, recurring, budget and goal
/// services already calculate; it has no financial logic of its own.
/// </summary>
public sealed partial class OverviewViewModel(
    AccountService accounts,
    DashboardService dashboard,
    ForecastService forecasts,
    RecurringService recurring,
    BudgetService budgets,
    GoalService goals,
    AutoRecordService autoRecord,
    NavigationService navigation,
    TimeProvider time,
    ILogger<OverviewViewModel> logger) : ObservableObject
{
    /// <summary>How far ahead "Upcoming" looks, and how many items it shows.</summary>
    private const int UpcomingDays = 14, UpcomingMax = 6, BudgetMax = 5, GoalsMax = 4;

    public string Greeting => time.GetLocalNow().Hour switch
    {
        < 12 => "Good morning",
        < 18 => "Good afternoon",
        _ => "Good evening",
    };

    public string TodayText => time.GetLocalNow().ToString("dddd, MMMM d", System.Globalization.CultureInfo.GetCultureInfo("en-US"));

    public ObservableCollection<MonthLineViewModel> MonthLines { get; } = [];
    public ObservableCollection<ForecastRowViewModel> Upcoming { get; } = [];
    public ObservableCollection<BudgetBriefViewModel> Budget { get; } = [];
    public ObservableCollection<GoalCardViewModel> Goals { get; } = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowEmptyState), nameof(ShowContent))]
    public partial bool IsLoaded { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowEmptyState), nameof(ShowContent))]
    public partial bool HasAccounts { get; set; }

    [ObservableProperty] public partial bool HasError { get; set; }

    [ObservableProperty] public partial string AvailableText { get; set; } = "";
    [ObservableProperty] public partial string AvailableCaption { get; set; } = "";
    [ObservableProperty] public partial string FreeToSpendText { get; set; } = "";
    [ObservableProperty] public partial bool HasFunds { get; set; }
    [ObservableProperty] public partial string NetWorthText { get; set; } = "";
    [ObservableProperty] public partial string MonthTitle { get; set; } = "This month";
    [ObservableProperty] public partial string RemainingText { get; set; } = "";
    [ObservableProperty] public partial bool IsRemainingNegative { get; set; }

    [ObservableProperty] public partial bool HasUpcoming { get; set; }
    [ObservableProperty] public partial string UpcomingEmptyText { get; set; } = "";
    [ObservableProperty] public partial string NeedsRecordingText { get; set; } = "";
    [ObservableProperty] public partial bool HasNeedsRecording { get; set; }
    [ObservableProperty] public partial string LowestText { get; set; } = "";
    [ObservableProperty] public partial bool IsLowestNegative { get; set; }

    [ObservableProperty] public partial bool HasBudget { get; set; }
    [ObservableProperty] public partial string BudgetCaption { get; set; } = "";

    [ObservableProperty] public partial bool HasGoals { get; set; }

    public bool ShowEmptyState => IsLoaded && !HasAccounts;
    public bool ShowContent => IsLoaded && HasAccounts;

    [RelayCommand]
    public async Task LoadAsync()
    {
        HasError = false;
        try
        {
            HasAccounts = await accounts.AnyAccountsAsync();
            if (HasAccounts)
            {
                var today = DateOnly.FromDateTime(time.GetLocalNow().Date);
                await autoRecord.RunAsync(); // so "Available" includes anything due today
                // One forecast feeds both "Available" (its starting point) and "Next two weeks".
                var forecast = await forecasts.GetForecastAsync(today, UpcomingDays);
                await LoadMoneyAsync(today, forecast);
                await LoadUpcomingAsync(today, forecast);
                await LoadBudgetAsync(today);
                await LoadGoalsAsync();
            }
            IsLoaded = true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load overview");
            HasError = true;
        }
    }

    private async Task LoadMoneyAsync(DateOnly today, Forecast forecast)
    {
        // "Available" is the forecast's starting point: actual money in every account except credit cards.
        AvailableText = Money.Format(forecast.StartingBalanceCents);
        AvailableCaption = forecast.Accounts.Count == 0
            ? "No accounts besides credit cards"
            : $"In {string.Join(", ", forecast.Accounts.Select(a => a.Name))}";
        // Sinking funds: money earmarked for known costs, shown beneath Available (which keeps its meaning).
        var funds = (await goals.GetFundsAsync()).Sum(f => f.CurrentCents);
        HasFunds = funds > 0;
        var free = forecast.StartingBalanceCents - funds;
        FreeToSpendText = HasFunds
            ? $"Set aside in funds −{Money.Format(funds)}  →  Free to spend {Money.Format(free)}" +
              (free < 0 ? ". More is set aside than you have: some of it isn't in your accounts yet." : "")
            : "";
        var worth = await accounts.GetNetWorthAsync(today);
        NetWorthText = $"Net worth {Money.Format(worth.NetWorthCents)}" +
                       (worth.LiabilitiesCents != 0 ? $"  ·  owed on cards {Money.Format(worth.LiabilitiesCents)}" : "");

        var month = await dashboard.GetMonthSummaryAsync(today);
        MonthTitle = $"{today.ToString("MMMM", System.Globalization.CultureInfo.GetCultureInfo("en-US"))} so far";
        MonthLines.Clear();
        MonthLines.Add(new("Income", Money.Format(month.IncomeCents), false));
        MonthLines.Add(new("Spending", Money.Format(-month.ExpenseCents), false));
        MonthLines.Add(new("Saved", Money.Format(month.SavedCents), false));
        RemainingText = Money.Format(month.RemainingCents);
        IsRemainingNegative = month.RemainingCents < 0;
    }

    private async Task LoadUpcomingAsync(DateOnly today, Forecast forecast)
    {
        Upcoming.Clear();
        foreach (var e in forecast.Events.Take(UpcomingMax))
        {
            Upcoming.Add(new ForecastRowViewModel(e, today));
        }
        HasUpcoming = Upcoming.Count > 0;
        UpcomingEmptyText = "Nothing expected in the next two weeks. Add bills and income on the Bills page.";

        var lowest = forecast.Lowest;
        IsLowestNegative = lowest.Cents < 0;
        LowestText = forecast.Events.Count == 0 ? ""
            : lowest.Cents < 0 ? $"Heads up: forecast to go below zero ({Money.Format(lowest.Cents)}) on {RecurrenceDisplay.ShortDate(lowest.Date)}."
            : $"Lowest point in the next two weeks: {Money.Format(lowest.Cents)}.";

        var missed = (await recurring.GetOccurrencesAsync(today.AddDays(-RecurringService.NotRecordedLookBackDays), today.AddDays(-1)))
            .Count(o => o.State == OccurrenceState.NotRecorded);
        HasNeedsRecording = missed > 0;
        NeedsRecordingText = missed == 1 ? "1 bill needs recording" : $"{missed} bills need recording";
    }

    private async Task LoadBudgetAsync(DateOnly today)
    {
        var month = await budgets.GetMonthAsync(today.Year, today.Month);
        Budget.Clear();
        var planned = month.Lines.Where(l => l.PlannedCents > 0)
            .OrderByDescending(l => l.Status == BudgetLineStatus.Over)
            .ThenByDescending(l => l.FractionUsed)
            .Take(BudgetMax);
        foreach (var line in planned)
        {
            Budget.Add(new BudgetBriefViewModel(line));
        }
        HasBudget = month.HasPlan;
        BudgetCaption = month.HasPlan
            ? $"{Money.Format(month.ActualCents)} spent of {Money.Format(month.PlannedCents)} planned"
            : "No plan for this month yet.";
    }

    private async Task LoadGoalsAsync()
    {
        Goals.Clear();
        foreach (var g in (await goals.GetGoalsAsync()).Take(GoalsMax))
        {
            Goals.Add(new GoalCardViewModel(g));
        }
        HasGoals = Goals.Count > 0;
    }

    [RelayCommand] private void GoToAccounts() => navigation.Navigate(AppPage.Accounts);
    [RelayCommand] private void GoToBills() => navigation.Navigate(AppPage.Bills);
    [RelayCommand] private void GoToBudget() => navigation.Navigate(AppPage.Budget);
    [RelayCommand] private void GoToGoals() => navigation.Navigate(AppPage.Goals);
    [RelayCommand] private void GoToTransactions() => navigation.Navigate(AppPage.Transactions);
}

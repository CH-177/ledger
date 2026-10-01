using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ledger.App.Presentation;
using Ledger.Core;
using Ledger.Core.Services;
using Microsoft.Extensions.Logging;

namespace Ledger.App.ViewModels;

/// <summary>A spending group's share of a month.</summary>
public sealed record CategoryShareViewModel(string Name, string AmountText, string ShareText, double Percent)
{
    public override string ToString() => $"{Name}, {AmountText}, {ShareText}";
}

/// <summary>One month of cash flow, with bars scaled to the largest month shown.</summary>
public sealed record MonthFlowViewModel(string MonthText, string IncomeText, string SpendingText, string NetText, bool IsNetNegative,
    double IncomeBar, double SpendingBar)
{
    public bool IsNetNotNegative => !IsNetNegative;
    public override string ToString() => $"{MonthText}: income {IncomeText}, spending {SpendingText}, net {NetText}";
}

/// <summary>Net worth at one point, with a bar scaled to the largest shown.</summary>
public sealed record NetWorthPointViewModel(string DateText, string AmountText, double Bar, bool IsNegative)
{
    public override string ToString() => $"{DateText}: {AmountText}";
}

public sealed record AccountBalanceViewModel(string Name, string TypeText, string BalanceText);

/// <summary>
/// Simple reports (brief §20): spending by category, cash flow, net worth and balances. Every number
/// comes from the budget, report and account services, so reports never disagree with other pages.
/// </summary>
public sealed partial class ReportsViewModel(
    BudgetService budgets,
    ReportService reports,
    AccountService accounts,
    TimeProvider time,
    ILogger<ReportsViewModel> logger) : ObservableObject
{
    private const int Months = 6;
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");
    private DateOnly _month;

    public ObservableCollection<CategoryShareViewModel> Categories { get; } = [];
    public ObservableCollection<MonthFlowViewModel> CashFlow { get; } = [];
    public ObservableCollection<NetWorthPointViewModel> NetWorth { get; } = [];
    public ObservableCollection<AccountBalanceViewModel> Balances { get; } = [];

    [ObservableProperty] public partial string MonthText { get; set; } = "";
    [ObservableProperty] public partial string SpendingTotalText { get; set; } = "";
    [ObservableProperty] public partial bool HasCategories { get; set; }
    [ObservableProperty] public partial bool CanGoNext { get; set; }
    [ObservableProperty] public partial string CashFlowSummary { get; set; } = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowEmpty), nameof(ShowContent))]
    public partial bool IsLoaded { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowEmpty), nameof(ShowContent))]
    public partial bool HasAccounts { get; set; }

    [ObservableProperty] public partial bool HasError { get; set; }

    public bool ShowEmpty => IsLoaded && !HasAccounts;
    public bool ShowContent => IsLoaded && HasAccounts;

    private DateOnly Today => DateOnly.FromDateTime(time.GetLocalNow().Date);

    [RelayCommand]
    public async Task LoadAsync()
    {
        HasError = false;
        try
        {
            var today = Today;
            if (_month == default)
            {
                _month = new DateOnly(today.Year, today.Month, 1);
            }
            HasAccounts = await accounts.AnyAccountsAsync();
            if (HasAccounts)
            {
                await LoadCategoriesAsync(today);
                await LoadCashFlowAsync(today);
                await LoadNetWorthAsync(today);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load reports");
            HasError = true;
        }
        finally
        {
            IsLoaded = true;
        }
    }

    [RelayCommand]
    private async Task PreviousMonthAsync()
    {
        _month = _month.AddMonths(-1);
        await LoadCategoriesAsync(Today);
    }

    [RelayCommand]
    private async Task NextMonthAsync()
    {
        _month = _month.AddMonths(1);
        await LoadCategoriesAsync(Today);
    }

    private async Task LoadCategoriesAsync(DateOnly today)
    {
        MonthText = _month.ToString("MMMM yyyy", Culture);
        CanGoNext = _month < new DateOnly(today.Year, today.Month, 1);

        // The budget's "spent" figures: same rules, same numbers as the Budget page.
        var month = await budgets.GetMonthAsync(_month.Year, _month.Month);
        var spent = month.Lines.Where(l => l.ActualCents > 0).OrderByDescending(l => l.ActualCents).ToList();
        var total = spent.Sum(l => l.ActualCents);
        Categories.Clear();
        foreach (var line in spent)
        {
            var share = total == 0 ? 0 : (double)line.ActualCents / total;
            Categories.Add(new CategoryShareViewModel(line.Name, Money.Format(line.ActualCents), $"{share:P0}", share * 100));
        }
        HasCategories = Categories.Count > 0;
        SpendingTotalText = HasCategories ? $"{Money.Format(total)} spent" : "No spending recorded this month.";
    }

    private async Task LoadCashFlowAsync(DateOnly today)
    {
        var flow = await reports.GetCashFlowAsync(today, Months);
        var max = Math.Max(1, flow.Max(f => Math.Max(f.IncomeCents, f.ExpenseCents)));
        CashFlow.Clear();
        foreach (var f in flow)
        {
            var name = new DateOnly(f.Year, f.Month, 1).ToString("MMM yyyy", Culture) + (f.IsPartial ? " (so far)" : "");
            CashFlow.Add(new MonthFlowViewModel(name, Money.Format(f.IncomeCents), Money.Format(f.ExpenseCents),
                Money.Format(f.NetCents, MoneySign.Always), f.NetCents < 0, 100.0 * f.IncomeCents / max, 100.0 * Math.Max(0, f.ExpenseCents) / max));
        }
        var net = flow.Sum(f => f.NetCents);
        CashFlowSummary = $"Over {Months} months: {Money.Format(flow.Sum(f => f.IncomeCents))} in, {Money.Format(flow.Sum(f => f.ExpenseCents))} spent, " +
                          $"{(net >= 0 ? "kept" : "short")} {Money.Format(Math.Abs(net))}. Transfers aren't counted.";
    }

    private async Task LoadNetWorthAsync(DateOnly today)
    {
        var history = await reports.GetNetWorthHistoryAsync(today, Months);
        var max = Math.Max(1, history.Max(h => Math.Abs(h.NetWorthCents)));
        NetWorth.Clear();
        foreach (var h in history)
        {
            var label = h.AsOf == today ? "Today" : $"End of {h.AsOf.ToString("MMM", Culture)}";
            NetWorth.Add(new NetWorthPointViewModel(label, Money.Format(h.NetWorthCents), 100.0 * Math.Abs(h.NetWorthCents) / max, h.NetWorthCents < 0));
        }

        Balances.Clear();
        foreach (var b in await accounts.GetAccountBalancesAsync())
        {
            var shown = b.Account.IsLiability ? -b.BalanceCents : b.BalanceCents;
            Balances.Add(new AccountBalanceViewModel(b.Account.Name, AccountTypeDisplay.For(b.Account.AccountType).Name + (b.Account.IsLiability ? " · owed" : ""),
                Money.Format(shown)));
        }
    }
}

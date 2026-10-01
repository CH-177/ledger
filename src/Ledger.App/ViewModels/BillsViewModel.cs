using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ledger.App.Presentation;
using Ledger.Core;
using Ledger.Core.Forecasting;
using Ledger.Core.Models;
using Ledger.Core.Services;
using Microsoft.Extensions.Logging;

namespace Ledger.App.ViewModels;

/// <summary>A summary card above the forecast.</summary>
public sealed record ForecastCardViewModel(string Title, string AmountText, string Caption, bool IsActual, bool IsWarning)
{
    public bool IsForecast => !IsActual;
}

/// <summary>One expected event in the forecast, with the balance it would leave.</summary>
public sealed class ForecastRowViewModel(ForecastEvent e, DateOnly today)
{
    public ForecastEvent Event { get; } = e;

    public string DateText =>
        Event.Date == today ? "Today" : Event.Date == today.AddDays(1) ? "Tomorrow" : RecurrenceDisplay.ShortDate(Event.Date);

    public string Name => Event.Name;

    public string DetailText => Event switch
    {
        { IsOverdue: true, Kind: ForecastEventKind.Paycheck } =>
            $"Payday was {RecurrenceDisplay.ShortDate(Event.ScheduledDate!.Value)}  ·  record it on the Income page",
        { IsOverdue: true } => $"Was due {RecurrenceDisplay.ShortDate(Event.ScheduledDate!.Value)}  ·  not recorded yet",
        { Kind: ForecastEventKind.Paycheck } => "Estimated from your shifts",
        { Kind: ForecastEventKind.Scheduled } => "Already recorded, dated ahead",
        { IsEstimated: true } => "Estimated amount",
        _ => "",
    };

    public bool HasDetail => DetailText.Length > 0;
    public string AmountText => Money.Format(Event.SignedCents, MoneySign.Always);
    public bool IsMoneyIn => Event.SignedCents > 0;
    public bool IsMoneyOut => !IsMoneyIn;
    public string BalanceText => Money.Format(Event.BalanceAfterCents);
    public bool IsBelowZero => Event.BalanceAfterCents < 0;
    public bool IsNotBelowZero => !IsBelowZero;
    public bool IsRecurring => Event.Kind == ForecastEventKind.Recurring;
    public bool IsPaycheck => Event.Kind == ForecastEventKind.Paycheck;
    public string MoreOptionsName => $"Options for {Name} on {DateText}";

    public override string ToString() =>
        $"{DateText}, {Name}, forecast {AmountText}, balance after about {BalanceText}" +
        (IsBelowZero ? ", below zero" : "") + (HasDetail ? $", {DetailText}" : "");
}

/// <summary>A past occurrence that still needs to be recorded or skipped.</summary>
public sealed class DueRowViewModel(RecurringOccurrence occurrence)
{
    public RecurringOccurrence Occurrence { get; } = occurrence;
    public string Name => Occurrence.Name;
    public string DueText => $"Was due {RecurrenceDisplay.ShortDate(Occurrence.Date)}";
    public string AmountText => (Occurrence.Rule.IsAmountEstimated ? "About " : "") + Money.Format(Occurrence.AmountCents);
    public string RecordName => $"Record {Name} for {RecurrenceDisplay.ShortDate(Occurrence.Date)}";
    public string SkipName => $"Skip {Name} for {RecurrenceDisplay.ShortDate(Occurrence.Date)}";
}

/// <summary>One recurring rule in the list.</summary>
public sealed class RuleRowViewModel(RecurringTransaction rule, DateOnly? next)
{
    public RecurringTransaction Rule { get; } = rule;
    public string Name => Rule.Name;

    public string AmountText => (Rule.IsAmountEstimated ? "About " : "") + Money.Format(Rule.AmountCents);

    public string ScheduleText
    {
        get
        {
            var accounts = Rule.TransactionType == TransactionType.Transfer
                ? $"{Rule.Account?.Name} → {Rule.TransferAccount?.Name}"
                : Rule.Account?.Name;
            var parts = new List<string?> { RecurrenceDisplay.Describe(Rule.Frequency, Rule.StartDate), accounts };
            if (Rule.Category is { } c)
            {
                parts.Add(c.ParentCategory is { } p ? $"{p.Name} › {c.Name}" : c.Name);
            }
            if (Rule.AutoRecordSince is not null)
            {
                parts.Add("Records automatically");
            }
            return string.Join("  ·  ", parts.Where(p => !string.IsNullOrEmpty(p)));
        }
    }

    public string NextText => !Rule.IsActive ? "Paused"
        : next is { } d ? $"Next: {RecurrenceDisplay.ShortDate(d)}"
        : "Ended";

    public bool IsPaused => !Rule.IsActive;

    /// <summary>"≈ $49.83 a month" for anything not already monthly.</summary>
    public string MonthlyText => Rule.Frequency == RecurrenceFrequency.Monthly ? ""
        : $"≈ {Money.Format(RecurrenceSchedule.MonthlyEquivalent(Rule.AmountCents, Rule.Frequency))} a month";

    public string PauseText => Rule.IsActive ? "Pause" : "Resume";
    public string MoreOptionsName => $"More options for {Name}";

    public override string ToString() => $"{Name}, {AmountText}, {ScheduleText}, {NextText}";
}

/// <summary>One repeat Ledger spotted in your history, offered as a bill.</summary>
public sealed class RepeatRowViewModel(RepeatSuggestion suggestion, string accountName)
{
    private static readonly System.Globalization.CultureInfo Culture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
    public RepeatSuggestion Suggestion { get; } = suggestion;
    public string Name => Suggestion.Payee;
    public string AmountText => (Suggestion.AmountVaries ? "about " : "") + Money.Format(Suggestion.TypicalCents);
    public string DetailText =>
        $"{RecurrenceDisplay.Describe(Suggestion.Frequency, Suggestion.NextDate)}, {(Suggestion.Type == TransactionType.Income ? "into" : "from")} {accountName}. " +
        $"You've entered it {Suggestion.Occurrences} times since {Suggestion.FirstDate.ToString("MMM d", Culture)}; next would be {RecurrenceDisplay.ShortDate(Suggestion.NextDate)}.";
    public string MakeName => $"Make {Name} a {(Suggestion.Type == TransactionType.Income ? "recurring income" : "bill")}";
    public string DismissName => $"Don't suggest {Name} again";
    public string MakeText => Suggestion.Type == TransactionType.Income ? "Make it recurring" : "Make it a bill";
}

public sealed partial class BillsViewModel(
    RecurringService recurring,
    SuggestionService suggestions,
    ForecastService forecasts,
    AccountService accounts,
    Services.AutoRecordService autoRecord,
    TimeProvider time,
    ILogger<BillsViewModel> logger) : ObservableObject
{
    public static readonly int[] RangeDays = [14, 30, 60, 90];

    public IReadOnlyList<string> RangeOptions { get; } = RangeDays.Select(d => $"Next {d} days").ToList();
    public ObservableCollection<FilterOption<int?>> AccountOptions { get; } = [];

    public ObservableCollection<ForecastCardViewModel> Cards { get; } = [];
    public ObservableCollection<ForecastRowViewModel> Forecast { get; } = [];
    public ObservableCollection<DueRowViewModel> Due { get; } = [];
    public ObservableCollection<RuleRowViewModel> Bills { get; } = [];
    public ObservableCollection<RuleRowViewModel> IncomeRules { get; } = [];
    public ObservableCollection<RuleRowViewModel> Transfers { get; } = [];
    public ObservableCollection<RepeatRowViewModel> Repeats { get; } = [];
    [ObservableProperty] public partial bool HasRepeats { get; set; }

    private IReadOnlyList<RecurringTransaction> _rules = [];
    private bool _loadingLists;

    [ObservableProperty] public partial int RangeIndex { get; set; } = 1;
    [ObservableProperty] public partial int AccountIndex { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowNoAccounts), nameof(ShowContent), nameof(ShowNoRules))]
    public partial bool IsLoaded { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowNoAccounts), nameof(ShowContent), nameof(ShowNoRules))]
    public partial bool HasAccounts { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowNoRules))]
    public partial bool HasRules { get; set; }

    [ObservableProperty] public partial bool HasDue { get; set; }
    [ObservableProperty] public partial bool HasForecast { get; set; }
    [ObservableProperty] public partial bool HasNoForecastEvents { get; set; }
    [ObservableProperty] public partial bool HasBills { get; set; }
    [ObservableProperty] public partial bool HasIncomeRules { get; set; }
    [ObservableProperty] public partial bool HasTransfers { get; set; }
    [ObservableProperty] public partial string ForecastCaption { get; set; } = "";
    [ObservableProperty] public partial string BillsCaption { get; set; } = "";
    [ObservableProperty] public partial string IncomeCaption { get; set; } = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => ErrorMessage is not null;
    public bool ShowNoAccounts => IsLoaded && !HasAccounts;
    public bool ShowContent => IsLoaded && HasAccounts;
    public bool ShowNoRules => IsLoaded && HasAccounts && !HasRules;

    private DateOnly Today => DateOnly.FromDateTime(time.GetLocalNow().Date);

    // Changing a picker refreshes only the forecast, and only after the ComboBox has finished its
    // own selection change: rebuilding a ComboBox's items from inside its SelectionChanged
    // re-enters the binding and overflowed the stack (1.4.0 crash).
    partial void OnRangeIndexChanged(int value)
    {
        if (!_loadingLists && value >= 0)
        {
            _ = RefreshForecastAsync();
        }
    }

    partial void OnAccountIndexChanged(int value)
    {
        if (!_loadingLists && value >= 0)
        {
            _ = RefreshForecastAsync();
        }
    }

    private async Task RefreshForecastAsync()
    {
        await Task.Yield();
        try
        {
            ErrorMessage = null;
            await LoadForecastAsync(Today);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to refresh the forecast");
            ErrorMessage = "Ledger couldn't update the forecast. Your data hasn't been changed.";
        }
    }

    public RecurringTransaction? RuleFor(int? id) => _rules.FirstOrDefault(r => r.Id == id);

    [RelayCommand]
    public async Task LoadAsync()
    {
        ErrorMessage = null;
        try
        {
            var today = Today;
            await autoRecord.RunAsync();
            RefreshAutoRecordNotice();
            await LoadAccountOptionsAsync();
            HasAccounts = AccountOptions.Count > 1;

            _rules = await recurring.GetRulesAsync();
            HasRules = _rules.Count > 0;
            var occurrences = await recurring.GetOccurrencesAsync(today.AddDays(-RecurringService.NotRecordedLookBackDays), today.AddYears(2));

            Due.Clear();
            foreach (var o in occurrences.Where(o => o.State == OccurrenceState.NotRecorded))
            {
                Due.Add(new DueRowViewModel(o));
            }
            HasDue = Due.Count > 0;

            FillRules(Bills, TransactionType.Expense, occurrences, today);
            FillRules(IncomeRules, TransactionType.Income, occurrences, today);
            FillRules(Transfers, TransactionType.Transfer, occurrences, today);
            HasBills = Bills.Count > 0;
            HasIncomeRules = IncomeRules.Count > 0;
            HasTransfers = Transfers.Count > 0;
            BillsCaption = MonthlyCaption(TransactionType.Expense, "in bills");
            await LoadRepeatsAsync(today);
            IncomeCaption = MonthlyCaption(TransactionType.Income, "expected");

            if (HasAccounts)
            {
                await LoadForecastAsync(today);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load bills");
            ErrorMessage = "Ledger couldn't load your bills. Your data hasn't been changed.";
        }
        finally
        {
            IsLoaded = true;
        }
    }

    private async Task LoadForecastAsync(DateOnly today)
    {
        var days = RangeDays[Math.Clamp(RangeIndex, 0, RangeDays.Length - 1)];
        var accountId = AccountOptions.ElementAtOrDefault(AccountIndex)?.Value;
        var forecast = await forecasts.GetForecastAsync(today, days, accountId);

        ForecastCaption = accountId is null
            ? $"Across {string.Join(", ", forecast.Accounts.Select(a => a.Name))}. Credit cards are left out: that money is owed, not yours to spend."
            : $"{forecast.Accounts.FirstOrDefault()?.Name} only.";

        var lowest = forecast.Lowest;
        Cards.Clear();
        Cards.Add(new("Balance today", Money.Format(forecast.StartingBalanceCents), "From your recorded transactions", IsActual: true, IsWarning: false));
        Cards.Add(new("Expected in", Money.Format(forecast.ExpectedInCents, MoneySign.Always), $"Forecast through {RecurrenceDisplay.ShortDate(forecast.Through)}", false, false));
        Cards.Add(new("Expected out", Money.Format(-forecast.ExpectedOutCents, MoneySign.Always), $"Forecast through {RecurrenceDisplay.ShortDate(forecast.Through)}", false, false));
        Cards.Add(new("Lowest point", Money.Format(lowest.Cents),
            lowest.Cents < 0 ? $"Below zero on {RecurrenceDisplay.ShortDate(lowest.Date)}"
            : lowest.Date == today ? "Never lower than today" : $"On {RecurrenceDisplay.ShortDate(lowest.Date)}",
            false, lowest.Cents < 0));

        Forecast.Clear();
        foreach (var e in forecast.Events)
        {
            Forecast.Add(new ForecastRowViewModel(e, today));
        }
        HasForecast = Forecast.Count > 0;
        HasNoForecastEvents = !HasForecast;
    }

    private async Task LoadAccountOptionsAsync()
    {
        var previous = AccountOptions.ElementAtOrDefault(AccountIndex)?.Value;
        var open = (await accounts.GetAccountBalancesAsync()).Select(b => b.Account).ToList();

        // Leave the list alone unless the accounts actually changed, so the selection stays put.
        var wanted = open.Select(a => (a.Name, (int?)a.Id)).Prepend(("All accounts except credit cards", null)).ToList();
        if (AccountOptions.Select(o => (o.Label, o.Value)).SequenceEqual(wanted))
        {
            return;
        }

        _loadingLists = true;
        try
        {
            AccountOptions.Clear();
            AccountOptions.Add(new("All accounts except credit cards", null));
            foreach (var a in open)
            {
                AccountOptions.Add(new(a.Name, a.Id));
            }
            AccountIndex = Math.Max(0, AccountOptions.ToList().FindIndex(o => o.Value == previous));
        }
        finally
        {
            _loadingLists = false;
        }
    }

    private async Task LoadRepeatsAsync(DateOnly today)
    {
        Repeats.Clear();
        try
        {
            foreach (var r in await suggestions.FindRepeatsAsync(today))
            {
                Repeats.Add(new RepeatRowViewModel(r, AccountOptions.FirstOrDefault(o => o.Value == r.AccountId)?.Label ?? "an account"));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Spotting repeats failed");
        }
        HasRepeats = Repeats.Count > 0;
    }

    public async Task DismissRepeatAsync(RepeatRowViewModel row)
    {
        await suggestions.DismissRepeatAsync(row.Suggestion.Key);
        Repeats.Remove(row);
        HasRepeats = Repeats.Count > 0;
    }

    private void FillRules(ObservableCollection<RuleRowViewModel> target, TransactionType type, IReadOnlyList<RecurringOccurrence> occurrences, DateOnly today)
    {
        target.Clear();
        foreach (var rule in _rules.Where(r => r.TransactionType == type))
        {
            target.Add(new RuleRowViewModel(rule, RecurringService.NextDate(rule, occurrences, today)));
        }
    }

    private string MonthlyCaption(TransactionType type, string what)
    {
        var active = _rules.Where(r => r.TransactionType == type && r.IsActive).ToList();
        if (active.Count == 0)
        {
            return "";
        }
        var monthly = active.Sum(r => RecurrenceSchedule.MonthlyEquivalent(r.AmountCents, r.Frequency));
        var approx = active.Any(r => r.IsAmountEstimated || r.Frequency != RecurrenceFrequency.Monthly) ? "About " : "";
        return $"{approx}{Money.Format(monthly)} a month {what}";
    }

    /// <summary>"Recorded automatically: Spotify $11.99 (Oct 1), …" until dismissed.</summary>
    [ObservableProperty] public partial string AutoRecordedText { get; set; } = "";
    [ObservableProperty] public partial bool HasAutoRecorded { get; set; }
    [ObservableProperty] public partial string AutoFailedText { get; set; } = "";
    [ObservableProperty] public partial bool HasAutoFailed { get; set; }

    private void RefreshAutoRecordNotice()
    {
        AutoRecordedText = string.Join(", ", autoRecord.Recorded.Select(r =>
            $"{r.Name} {Money.Format(r.AmountCents)} ({r.Date.ToString("MMM d", System.Globalization.CultureInfo.GetCultureInfo("en-US"))})"));
        HasAutoRecorded = autoRecord.Recorded.Count > 0;
        AutoFailedText = string.Join(" ", autoRecord.Failed.Select(f =>
            $"{f.Name} ({f.Date.ToString("MMM d", System.Globalization.CultureInfo.GetCultureInfo("en-US"))}): {f.Reason}"));
        HasAutoFailed = autoRecord.Failed.Count > 0;
    }

    [RelayCommand]
    private void DismissAutoRecord()
    {
        autoRecord.Dismiss();
        RefreshAutoRecordNotice();
    }

    /// <summary>Skips everything under "Needs recording".</summary>
    public async Task SkipAllDueAsync() =>
        await RunAsync(() => recurring.SkipManyAsync(Due.Select(d => (d.Occurrence.Rule.Id, d.Occurrence.Date)).ToList()), "skip those payments");

    public async Task SkipAsync(int ruleId, DateOnly date) => await RunAsync(() => recurring.SkipAsync(ruleId, date), "skip that payment");

    public async Task RecordAsync(int ruleId, DateOnly date, int transactionId) =>
        await RunAsync(() => recurring.RecordAsync(ruleId, date, transactionId), "link that transaction");

    public async Task TogglePausedAsync(RuleRowViewModel row) =>
        await RunAsync(() => recurring.SetActiveAsync(row.Rule.Id, !row.Rule.IsActive), row.Rule.IsActive ? "pause it" : "resume it");

    public async Task DeleteAsync(RuleRowViewModel row) => await RunAsync(() => recurring.DeleteAsync(row.Rule.Id), "delete it");

    /// <summary>Runs a change, then reloads. A validation message is shown as it is; anything else is logged.</summary>
    private async Task RunAsync(Func<Task> change, string what)
    {
        string? error = null;
        try
        {
            await change();
        }
        catch (LedgerValidationException ex)
        {
            error = ex.Message;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Recurring change failed");
            error = $"Ledger couldn't {what}. Your data hasn't been changed.";
        }
        await LoadAsync();
        ErrorMessage ??= error;
    }
}

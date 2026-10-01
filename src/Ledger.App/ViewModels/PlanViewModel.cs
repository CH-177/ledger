using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Ledger.App.Presentation;
using Ledger.Core;
using Ledger.Core.Forecasting;
using Ledger.Core.Models;
using Ledger.Core.Services;
using Microsoft.Extensions.Logging;

namespace Ledger.App.ViewModels;

/// <summary>One assumption in the scenario being built, described in words.</summary>
public sealed class ScenarioChangeRowViewModel(ScenarioChange change, string description)
{
    public ScenarioChange Change { get; } = change;
    public string Description { get; } = description;
    public string RemoveName => $"Remove: {Description}";

    /// <summary>Purchases, extra income and new recurring items can be made real (by you, in the usual editor).</summary>
    public bool CanMakeReal => Change.Kind is ScenarioChangeKind.Purchase or ScenarioChangeKind.ExtraIncome or ScenarioChangeKind.AddBill or ScenarioChangeKind.AddIncome;
    public string MakeRealName => $"Make real: {Description}";
}

/// <summary>A row of the before/after comparison.</summary>
public sealed record CompareRowViewModel(string Label, string Before, string After, bool Worse);

/// <summary>
/// What if? Try assumptions against the forecast (and "Can I afford this?"). Nothing here is ever
/// saved as real data; only named scenarios' assumptions are kept, to reopen.
/// </summary>
public sealed partial class PlanViewModel(
    ScenarioService scenarios,
    AccountService accounts,
    IncomeService income,
    RecurringService recurring,
    TimeProvider time,
    ILogger<PlanViewModel> logger) : ObservableObject
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");
    public static readonly int[] RangeDays = [30, 60, 90, 180];

    public static IReadOnlyList<(string Label, ScenarioChangeKind Kind)> KindOptions { get; } =
    [
        ("A one-off purchase", ScenarioChangeKind.Purchase),
        ("Extra money coming in", ScenarioChangeKind.ExtraIncome),
        ("Work more or fewer hours (%)", ScenarioChangeKind.HoursPercent),
        ("Work a set number of hours a week", ScenarioChangeKind.WeeklyHours),
        ("A new pay rate", ScenarioChangeKind.PayRate),
        ("A new bill", ScenarioChangeKind.AddBill),
        ("New recurring income", ScenarioChangeKind.AddIncome),
        ("Stop a bill or income", ScenarioChangeKind.StopRecurring),
    ];

    public IReadOnlyList<string> KindLabels { get; } = KindOptions.Select(k => k.Label).ToList();
    public IReadOnlyList<string> RangeOptions { get; } = RangeDays.Select(d => $"Next {d} days").ToList();
    public IReadOnlyList<string> FrequencyOptions { get; } = RecurrenceDisplay.Options.Select(o => o.Label).ToList();
    public ObservableCollection<FilterOption<int?>> AccountOptions { get; } = [];
    public ObservableCollection<FilterOption<int?>> JobOptions { get; } = [];
    public ObservableCollection<FilterOption<int?>> RuleOptions { get; } = [];
    public ObservableCollection<string> SavedOptions { get; } = [];
    public ObservableCollection<ScenarioChangeRowViewModel> Changes { get; } = [];
    public ObservableCollection<CompareRowViewModel> Comparison { get; } = [];
    public ObservableCollection<string> GoalImpacts { get; } = [];

    private IReadOnlyList<Scenario> _saved = [];
    private bool _loading;

    // ---- Can I afford this? -------------------------------------------------------------------------
    [ObservableProperty] public partial string AffordAmountText { get; set; } = "";
    [ObservableProperty] public partial DateTimeOffset? AffordDate { get; set; }
    [ObservableProperty] public partial int AffordAccountIndex { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasAffordAnswer))] public partial string? AffordAnswer { get; set; }
    [ObservableProperty] public partial bool AffordYes { get; set; }
    [ObservableProperty] public partial bool AffordNo { get; set; }
    public bool HasAffordAnswer => AffordAnswer is not null;

    // ---- Scenario -----------------------------------------------------------------------------------
    [ObservableProperty] public partial string ScenarioName { get; set; } = "";
    [ObservableProperty] public partial int SavedIndex { get; set; } = -1;
    [ObservableProperty] public partial int RangeIndex { get; set; } = 2;
    [ObservableProperty] public partial string CushionText { get; set; } = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(NeedsAmount), nameof(NeedsAccount), nameof(NeedsJob), nameof(NeedsPercent),
        nameof(NeedsWeeklyHours), nameof(NeedsFrequency), nameof(NeedsRule), nameof(NeedsName), nameof(AmountLabel))]
    public partial int KindIndex { get; set; }

    [ObservableProperty] public partial DateTimeOffset? ChangeDate { get; set; }
    [ObservableProperty] public partial string ChangeAmountText { get; set; } = "";
    [ObservableProperty] public partial int ChangeAccountIndex { get; set; }
    [ObservableProperty] public partial int ChangeJobIndex { get; set; }
    [ObservableProperty] public partial string PercentText { get; set; } = "75";
    [ObservableProperty] public partial string WeeklyHoursText { get; set; } = "20";
    [ObservableProperty] public partial int FrequencyIndex { get; set; } = 2;
    [ObservableProperty] public partial int RuleIndex { get; set; }
    [ObservableProperty] public partial string ChangeName { get; set; } = "";

    [ObservableProperty] public partial bool HasChanges { get; set; }
    [ObservableProperty] public partial bool HasResults { get; set; }
    [ObservableProperty] public partial bool HasGoalImpacts { get; set; }
    [ObservableProperty] public partial string ResultTitle { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] public partial string? ErrorMessage { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasMessage))] public partial string? Message { get; set; }
    public bool HasError => ErrorMessage is not null;
    public bool HasMessage => Message is not null;

    private ScenarioChangeKind SelectedKind => KindOptions[Math.Clamp(KindIndex, 0, KindOptions.Count - 1)].Kind;
    public bool NeedsAmount => SelectedKind is ScenarioChangeKind.Purchase or ScenarioChangeKind.ExtraIncome or ScenarioChangeKind.PayRate or ScenarioChangeKind.AddBill or ScenarioChangeKind.AddIncome;
    public bool NeedsAccount => SelectedKind is ScenarioChangeKind.Purchase or ScenarioChangeKind.ExtraIncome or ScenarioChangeKind.AddBill or ScenarioChangeKind.AddIncome;
    public bool NeedsJob => SelectedKind is ScenarioChangeKind.HoursPercent or ScenarioChangeKind.WeeklyHours or ScenarioChangeKind.PayRate;
    public bool NeedsPercent => SelectedKind == ScenarioChangeKind.HoursPercent;
    public bool NeedsWeeklyHours => SelectedKind == ScenarioChangeKind.WeeklyHours;
    public bool NeedsFrequency => SelectedKind is ScenarioChangeKind.AddBill or ScenarioChangeKind.AddIncome;
    public bool NeedsRule => SelectedKind == ScenarioChangeKind.StopRecurring;
    public bool NeedsName => SelectedKind is ScenarioChangeKind.Purchase or ScenarioChangeKind.ExtraIncome or ScenarioChangeKind.AddBill or ScenarioChangeKind.AddIncome;
    public string AmountLabel => SelectedKind == ScenarioChangeKind.PayRate ? "New hourly rate" : "Amount";

    private DateOnly Today => DateOnly.FromDateTime(time.GetLocalNow().Date);

    public async Task LoadAsync()
    {
        _loading = true;
        try
        {
            AccountOptions.Clear();
            foreach (var b in (await accounts.GetAccountBalancesAsync()).Where(b => !b.Account.IsLiability))
            {
                AccountOptions.Add(new($"{b.Account.Name}  ({Money.Format(b.BalanceCents)})", b.Account.Id));
            }
            JobOptions.Clear();
            JobOptions.Add(new("All jobs", null));
            foreach (var j in await income.GetJobsAsync()) JobOptions.Add(new(j.Name, j.Id));
            RuleOptions.Clear();
            foreach (var r in (await recurring.GetRulesAsync()).Where(r => r.IsActive)) RuleOptions.Add(new($"{r.Name} ({Money.Format(r.AmountCents)})", r.Id));
            var today = Today;
            AffordDate ??= new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue));
            ChangeDate ??= new DateTimeOffset(today.AddDays(7).ToDateTime(TimeOnly.MinValue));
            AffordAccountIndex = Math.Max(0, AffordAccountIndex);
            ChangeAccountIndex = Math.Max(0, ChangeAccountIndex);
            await LoadSavedAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Loading What if failed");
            ErrorMessage = "Ledger couldn't load your accounts.";
        }
        finally
        {
            _loading = false;
        }
        await RecalculateAsync();
    }

    private async Task LoadSavedAsync()
    {
        _saved = await scenarios.GetSavedAsync();
        SavedOptions.Clear();
        foreach (var s in _saved) SavedOptions.Add(s.Name);
    }

    private long Cushion => Money.TryParse(CushionText, out var c) && c > 0 ? c : 0;

    // ---- Can I afford this? -------------------------------------------------------------------------

    public async Task CheckAffordAsync()
    {
        ErrorMessage = null;
        AffordAnswer = null;
        AffordYes = AffordNo = false;
        if (!Money.TryParse(AffordAmountText, out var amount) || amount <= 0)
        {
            ErrorMessage = "Enter the amount, like 1200.";
            return;
        }
        if (AccountOptions.ElementAtOrDefault(AffordAccountIndex)?.Value is not { } account || AffordDate is not { } date)
        {
            ErrorMessage = "Choose the account and date.";
            return;
        }
        try
        {
            var answer = await scenarios.CanIAffordAsync(Today, amount, DateOnly.FromDateTime(date.Date), account, Cushion);
            AffordAnswer = answer.Answer + (Cushion > 0 ? $" (Your cushion: {Money.Format(Cushion)}.)" : "") +
                           " Based on your balances today, your bills, dated-ahead transactions and paychecks predicted from your shifts.";
            AffordYes = answer.Affordable;
            AffordNo = !answer.Affordable;
        }
        catch (LedgerValidationException ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    // ---- Scenario -----------------------------------------------------------------------------------

    public async Task AddChangeAsync()
    {
        ErrorMessage = null;
        if (ChangeDate is not { } when)
        {
            ErrorMessage = "Choose the date it starts.";
            return;
        }
        var from = DateOnly.FromDateTime(when.Date);
        var kind = SelectedKind;
        long amount = 0;
        if (NeedsAmount && (!Money.TryParse(ChangeAmountText, out amount) || amount <= 0))
        {
            ErrorMessage = kind == ScenarioChangeKind.PayRate ? "Enter the new hourly rate, like 15.00." : "Enter the amount, like 450.";
            return;
        }
        int? account = NeedsAccount ? AccountOptions.ElementAtOrDefault(ChangeAccountIndex)?.Value : null;
        if (NeedsAccount && account is null)
        {
            ErrorMessage = "Choose the account.";
            return;
        }
        var percent = 100;
        if (NeedsPercent && (!int.TryParse(PercentText.Trim().TrimEnd('%'), out percent) || percent is < 0 or > 400))
        {
            ErrorMessage = "Enter a percentage of your scheduled hours, like 75 or 125.";
            return;
        }
        var weeklyMinutes = 0;
        if (NeedsWeeklyHours)
        {
            if (!decimal.TryParse(WeeklyHoursText.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var hours) || hours is < 0 or > 100)
            {
                ErrorMessage = "Enter hours a week, like 20.";
                return;
            }
            weeklyMinutes = (int)Math.Round(hours * 60);
        }
        int? rule = NeedsRule ? RuleOptions.ElementAtOrDefault(RuleIndex)?.Value : null;
        if (NeedsRule && rule is null)
        {
            ErrorMessage = "There's no bill or recurring income to stop.";
            return;
        }
        var change = new ScenarioChange(kind, from, amount, account, NeedsJob ? JobOptions.ElementAtOrDefault(ChangeJobIndex)?.Value : null,
            percent, weeklyMinutes, RecurrenceDisplay.Options[Math.Clamp(FrequencyIndex, 0, RecurrenceDisplay.Options.Count - 1)].Value, rule,
            NeedsName && !string.IsNullOrWhiteSpace(ChangeName) ? ChangeName.Trim() : null);
        Changes.Add(new ScenarioChangeRowViewModel(change, Describe(change)));
        ChangeAmountText = "";
        ChangeName = "";
        HasChanges = true;
        await RecalculateAsync();
    }

    public async Task RemoveChangeAsync(ScenarioChangeRowViewModel row)
    {
        Changes.Remove(row);
        HasChanges = Changes.Count > 0;
        await RecalculateAsync();
    }

    protected override async void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading) return;
        if (e.PropertyName is nameof(RangeIndex) or nameof(CushionText)) await RecalculateAsync();
        if (e.PropertyName == nameof(SavedIndex) && SavedIndex >= 0 && SavedIndex < _saved.Count) await OpenSavedAsync(_saved[SavedIndex]);
    }

    private async Task OpenSavedAsync(Scenario scenario)
    {
        ScenarioName = scenario.Name;
        Changes.Clear();
        foreach (var c in scenario.Changes) Changes.Add(new ScenarioChangeRowViewModel(c, Describe(c)));
        HasChanges = Changes.Count > 0;
        Message = null;
        await RecalculateAsync();
    }

    private Scenario Current => new(ScenarioName.Trim(), Changes.Select(c => c.Change).ToList());

    public async Task RecalculateAsync()
    {
        Comparison.Clear();
        GoalImpacts.Clear();
        HasResults = HasGoalImpacts = false;
        if (Changes.Count == 0) return;
        try
        {
            var days = RangeDays[Math.Clamp(RangeIndex, 0, RangeDays.Length - 1)];
            var result = await scenarios.CompareAsync(Today, days, Current, Cushion);
            ResultTitle = $"Over the next {days} days, across your accounts except credit cards";
            string Low(ForecastOutcome o) => $"{Money.Format(o.LowestCents)} on {o.LowestDate.ToString("MMM d", Culture)}";
            string When(DateOnly? d) => d is { } x ? x.ToString("MMM d", Culture) : "Never";
            var (b, a) = (result.Before, result.After);
            Comparison.Add(new("Lowest point", Low(b), Low(a), a.LowestCents < b.LowestCents));
            Comparison.Add(new("Balance at the end", Money.Format(b.EndingCents), Money.Format(a.EndingCents), a.EndingCents < b.EndingCents));
            Comparison.Add(new("Goes below $0", When(b.BelowZeroOn), When(a.BelowZeroOn), a.BelowZeroOn is not null && (b.BelowZeroOn is null || a.BelowZeroOn < b.BelowZeroOn)));
            if (Cushion > 0)
            {
                Comparison.Add(new($"Goes below your {Money.Format(Cushion)} cushion", When(b.BelowCushionOn), When(a.BelowCushionOn),
                    a.BelowCushionOn is not null && (b.BelowCushionOn is null || a.BelowCushionOn < b.BelowCushionOn)));
            }
            foreach (var g in result.Goals)
            {
                GoalImpacts.Add($"{g.GoalName}: on {g.TargetDate.ToString("MMM d", Culture)} its account would hold {Money.Format(g.ScenarioCents)} instead of {Money.Format(g.BaselineCents)} " +
                                $"(target {Money.Format(g.TargetCents)}{(g.ScenarioCents < g.TargetCents && g.BaselineCents >= g.TargetCents ? ": it would no longer be reached" : "")}).");
            }
            HasGoalImpacts = GoalImpacts.Count > 0;
            HasResults = true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Comparing a scenario failed");
            ErrorMessage = "Ledger couldn't work out this scenario.";
        }
    }

    public async Task SaveAsync()
    {
        ErrorMessage = null;
        if (Changes.Count == 0)
        {
            ErrorMessage = "Add at least one change before saving the scenario.";
            return;
        }
        try
        {
            await scenarios.SaveAsync(Current);
            await LoadSavedAsync();
            _loading = true;
            SavedIndex = SavedOptions.ToList().FindIndex(n => string.Equals(n, ScenarioName.Trim(), StringComparison.OrdinalIgnoreCase));
            _loading = false;
            Message = $"Saved “{ScenarioName.Trim()}”. Only the assumptions are saved; nothing real changed.";
        }
        catch (LedgerValidationException ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    public async Task DeleteSavedAsync()
    {
        if (SavedIndex < 0 || SavedIndex >= _saved.Count) return;
        var name = _saved[SavedIndex].Name;
        await scenarios.DeleteAsync(name);
        await LoadSavedAsync();
        _loading = true;
        SavedIndex = -1;
        _loading = false;
        Message = $"Deleted the saved scenario “{name}”.";
    }

    public async Task ClearAsync()
    {
        Changes.Clear();
        HasChanges = false;
        ScenarioName = "";
        _loading = true;
        SavedIndex = -1;
        _loading = false;
        Message = null;
        await RecalculateAsync();
    }

    private string AccountName(int? id) => AccountOptions.FirstOrDefault(o => o.Value == id)?.Label.Split("  (")[0] ?? "an account";
    private string JobName(int? id) => id is null ? "all jobs" : JobOptions.FirstOrDefault(o => o.Value == id)?.Label ?? "a job";

    /// <summary>The assumption in words, so every one used is visible.</summary>
    private string Describe(ScenarioChange c)
    {
        var from = c.From.ToString("MMM d", Culture);
        var named = c.Name is { } n ? $" “{n}”" : "";
        var frequency = RecurrenceDisplay.Options.FirstOrDefault(o => o.Value == c.Frequency).Label?.ToLowerInvariant() ?? "monthly";
        return c.Kind switch
        {
            ScenarioChangeKind.Purchase => $"A {Money.Format(c.AmountCents)} purchase{named} on {from}, from {AccountName(c.AccountId)}",
            ScenarioChangeKind.ExtraIncome => $"{Money.Format(c.AmountCents)}{named} coming in on {from}, into {AccountName(c.AccountId)}",
            ScenarioChangeKind.HoursPercent => $"Scheduled shifts at {c.Percent}% of their length from {from} ({JobName(c.JobId)})",
            ScenarioChangeKind.WeeklyHours => $"{c.WeeklyMinutes / 60m:0.##} paid hours a week from {from} ({JobName(c.JobId)})",
            ScenarioChangeKind.PayRate => $"Pay of {Money.Format(c.AmountCents)} an hour from {from} ({JobName(c.JobId)})",
            ScenarioChangeKind.AddBill => $"A new bill{named} of {Money.Format(c.AmountCents)}, {frequency} from {from}, from {AccountName(c.AccountId)}",
            ScenarioChangeKind.AddIncome => $"New income{named} of {Money.Format(c.AmountCents)}, {frequency} from {from}, into {AccountName(c.AccountId)}",
            ScenarioChangeKind.StopRecurring => $"{RuleOptions.FirstOrDefault(o => o.Value == c.RecurringId)?.Label ?? "A recurring item"} stops from {from}",
            _ => c.Kind.ToString(),
        };
    }
}

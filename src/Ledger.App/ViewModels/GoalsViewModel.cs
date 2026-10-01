using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ledger.Core;
using Ledger.Core.Models;
using Ledger.Core.Services;
using Microsoft.Extensions.Logging;

namespace Ledger.App.ViewModels;

/// <summary>One goal as a calm card: amount, bar, percent, target date and a suggestion.</summary>
public sealed class GoalCardViewModel(GoalProgress progress)
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");

    public GoalProgress Progress { get; } = progress;
    public Goal Goal => Progress.Goal;
    public string Name => Goal.Name;

    public string AmountText => $"{Money.Format(Progress.CurrentCents)} of {Money.Format(Progress.TargetCents)}";
    public double Percent => Progress.Fraction * 100;
    public string PercentText => $"{Progress.Percent}%";

    public string TargetText => Goal.TargetDate is { } d ? $"Target: {d.ToString("MMMM yyyy", Culture)}" : "No target date";

    public string SuggestionText => Progress switch
    {
        { IsReached: true } => "Goal reached",
        { IsPastDue: true } => $"Target date passed  ·  {Money.Format(Progress.RemainingCents)} to go",
        { SuggestedMonthlyCents: { } monthly } => $"{Money.Format(monthly)}/month suggested",
        _ => $"{Money.Format(Progress.RemainingCents)} to go",
    };

    public bool IsReached => Progress.IsReached;
    public bool IsPastDue => Progress.IsPastDue;

    public string TrackingText => Goal.IsSinkingFund
        ? "Sinking fund" + (Goal.MonthlySetAsideCents is { } m ? $"  ·  {Money.Format(m)} a month" : "") + "  ·  spending can be paid from it"
        : Goal.Tracking == GoalTracking.AccountBalance
            ? $"Follows the {Goal.Account?.Name} balance"
            : Goal.Account is { } a ? $"Kept in {a.Name}" : "Updated by you";

    public bool IsManual => Goal.Tracking == GoalTracking.Manual;
    public bool IsArchived => Goal.IsArchived;
    public string ArchiveText => IsArchived ? "Restore" : "Archive";
    public string AddName => $"Add money to {Name}";
    public string MoreOptionsName => $"More options for {Name}";

    public override string ToString() => $"{Name}, {AmountText}, {PercentText}, {TargetText}, {SuggestionText}, {TrackingText}";
}

public sealed partial class GoalsViewModel(GoalService goals, ILogger<GoalsViewModel> logger) : ObservableObject
{
    public ObservableCollection<GoalCardViewModel> Goals { get; } = [];
    public ObservableCollection<GoalCardViewModel> Archived { get; } = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowEmpty), nameof(ShowContent))]
    public partial bool IsLoaded { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowEmpty), nameof(ShowContent))]
    public partial bool HasGoals { get; set; }

    [ObservableProperty] public partial bool HasArchived { get; set; }
    [ObservableProperty] public partial string SummaryText { get; set; } = "";
    [ObservableProperty] public partial string ArchivedHeader { get; set; } = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => ErrorMessage is not null;
    public bool ShowEmpty => IsLoaded && !HasGoals && !HasArchived;
    public bool ShowContent => IsLoaded && (HasGoals || HasArchived);

    [RelayCommand]
    public async Task LoadAsync()
    {
        ErrorMessage = null;
        try
        {
            var all = await goals.GetGoalsAsync(includeArchived: true);
            Goals.Clear();
            Archived.Clear();
            foreach (var p in all)
            {
                (p.Goal.IsArchived ? Archived : Goals).Add(new GoalCardViewModel(p));
            }
            HasGoals = Goals.Count > 0;
            HasArchived = Archived.Count > 0;
            ArchivedHeader = $"Archived ({Archived.Count})";

            var active = all.Where(p => !p.Goal.IsArchived).ToList();
            SummaryText = active.Count == 0 ? "" :
                $"{Money.Format(active.Sum(p => Math.Min(p.CurrentCents, p.TargetCents)))} saved toward {Money.Format(active.Sum(p => p.TargetCents))}" +
                $" across {active.Count} goal{(active.Count == 1 ? "" : "s")}";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load goals");
            ErrorMessage = "Ledger couldn't load your goals. Your data hasn't been changed.";
        }
        finally
        {
            IsLoaded = true;
        }
    }

    public Task ToggleArchivedAsync(GoalCardViewModel card) =>
        RunAsync(() => goals.SetArchivedAsync(card.Goal.Id, !card.IsArchived), card.IsArchived ? "restore it" : "archive it");

    public Task DeleteAsync(GoalCardViewModel card) => RunAsync(() => goals.DeleteAsync(card.Goal.Id), "delete it");

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
            logger.LogError(ex, "Goal change failed");
            error = $"Ledger couldn't {what}. Your data hasn't been changed.";
        }
        await LoadAsync();
        ErrorMessage ??= error;
    }
}

/// <summary>Adding money to, or taking it out of, a manual goal.</summary>
public sealed partial class GoalMoneyViewModel(GoalService goals, TimeProvider time, ILogger<GoalMoneyViewModel> logger) : ObservableObject
{
    private int _goalId;

    public string Title { get; private set; } = "";
    public string CurrentText { get; private set; } = "";

    /// <summary>0 = Add, 1 = Take out.</summary>
    [ObservableProperty] public partial int DirectionIndex { get; set; }
    [ObservableProperty] public partial string AmountText { get; set; } = "";
    [ObservableProperty] public partial DateTimeOffset? Date { get; set; }
    [ObservableProperty] public partial string Note { get; set; } = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty] public partial bool IsSaving { get; set; }
    public bool HasError => ErrorMessage is not null;

    public void Load(GoalProgress progress, bool takeOut = false)
    {
        _goalId = progress.Goal.Id;
        Title = progress.Goal.Name;
        CurrentText = $"{Money.Format(progress.CurrentCents)} set aside so far. This only tracks the goal; no account balance changes.";
        DirectionIndex = takeOut ? 1 : 0;
        Date = time.GetLocalNow().Date;
    }

    public async Task<bool> SaveAsync()
    {
        ErrorMessage = null;
        if (!Money.TryParse(AmountText, out var cents) || cents <= 0)
        {
            ErrorMessage = "Enter an amount greater than zero, like 25.00.";
            return false;
        }
        if (Date is not { } date)
        {
            ErrorMessage = "Choose a date.";
            return false;
        }

        IsSaving = true;
        try
        {
            await goals.AddContributionAsync(_goalId, DirectionIndex == 1 ? -cents : cents, DateOnly.FromDateTime(date.Date), Note);
            return true;
        }
        catch (LedgerValidationException ex)
        {
            ErrorMessage = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save goal contribution");
            ErrorMessage = "Something went wrong while saving. Your existing data has not been changed.";
            return false;
        }
        finally
        {
            IsSaving = false;
        }
    }
}

/// <summary>One line in a goal's history.</summary>
public sealed class GoalContributionRowViewModel(GoalContribution contribution)
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");
    public GoalContribution Contribution { get; } = contribution;
    public string DateText => Contribution.Date.ToString("MMM d, yyyy", Culture);
    public string AmountText => Money.Format(Contribution.AmountCents, MoneySign.Always);
    public string NoteText => Contribution.Note ?? (Contribution.AmountCents > 0 ? "Added" : "Taken out");
    public string RemoveName => $"Remove {AmountText} on {DateText}";
}

/// <summary>A manual goal's contributions, with a way to remove one entered by mistake.</summary>
public sealed partial class GoalHistoryViewModel(GoalService goals, ILogger<GoalHistoryViewModel> logger) : ObservableObject
{
    private int _goalId;

    public ObservableCollection<GoalContributionRowViewModel> Rows { get; } = [];
    public string Title { get; private set; } = "";
    public bool Changed { get; private set; }

    [ObservableProperty] public partial string StartingText { get; set; } = "";
    [ObservableProperty] public partial bool IsEmpty { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }
    public bool HasError => ErrorMessage is not null;

    public async Task LoadAsync(GoalProgress progress)
    {
        _goalId = progress.Goal.Id;
        Title = $"{progress.Goal.Name} history";
        StartingText = progress.Goal.StartingAmountCents > 0
            ? $"Started with {Money.Format(progress.Goal.StartingAmountCents)} already saved."
            : "";
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        Rows.Clear();
        foreach (var c in await goals.GetContributionsAsync(_goalId))
        {
            Rows.Add(new GoalContributionRowViewModel(c));
        }
        IsEmpty = Rows.Count == 0;
    }

    public async Task RemoveAsync(GoalContributionRowViewModel row)
    {
        ErrorMessage = null;
        try
        {
            await goals.DeleteContributionAsync(row.Contribution.Id);
            Changed = true;
        }
        catch (LedgerValidationException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to remove goal contribution");
            ErrorMessage = "Ledger couldn't remove that entry. Your data hasn't been changed.";
        }
        await ReloadAsync();
    }
}

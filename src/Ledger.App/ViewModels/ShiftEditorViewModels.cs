using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Ledger.Core;
using Ledger.Core.Models;
using Ledger.Core.Payroll;
using Ledger.Core.Services;
using Microsoft.Extensions.Logging;

namespace Ledger.App.ViewModels;

/// <summary>Formats hours for display: 240 → "4 h", 270 → "4.5 h", 265 → "4 h 25 m".</summary>
public static class HoursFormat
{
    public static string Of(int minutes) => minutes % 60 == 0 ? $"{minutes / 60} h"
        : minutes % 30 == 0 ? $"{minutes / 60.0:0.#} h"
        : $"{minutes / 60} h {minutes % 60} m";
}

/// <summary>Form state for adding or editing one shift.</summary>
public sealed partial class ShiftEditorViewModel(IncomeService income, TimeProvider time, ILogger<ShiftEditorViewModel> logger)
    : ObservableObject
{
    private int? _editingId;
    private readonly Dictionary<int, BreakRule> _rules = [];

    public ObservableCollection<FilterOption<int?>> JobOptions { get; } = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(PaidPreview))]
    public partial int JobIndex { get; set; }

    [ObservableProperty] public partial DateTimeOffset? Date { get; set; }

    /// <summary>0 = enter hours, 1 = enter start and end times.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(UsesHours), nameof(UsesTimes), nameof(PaidPreview))]
    public partial int ModeIndex { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(PaidPreview))]
    public partial string HoursText { get; set; } = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(PaidPreview))]
    public partial TimeSpan StartTime { get; set; } = new(16, 0, 0);

    [ObservableProperty, NotifyPropertyChangedFor(nameof(PaidPreview))]
    public partial TimeSpan EndTime { get; set; } = new(21, 0, 0);

    /// <summary>When on, <see cref="BreakText"/> replaces the job's break rule for this shift.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PaidPreview))]
    public partial bool HasBreakOverride { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(PaidPreview))]
    public partial string BreakText { get; set; } = "";

    [ObservableProperty] public partial bool IsWorked { get; set; }

    /// <summary>Live paid-time preview, e.g. "5.5 h paid (6 h shift − 30 min unpaid break)".</summary>
    public string PaidPreview
    {
        get
        {
            var scheduled = CurrentScheduledMinutes();
            if (scheduled is not > 0)
            {
                return "";
            }
            var rule = JobOptions.ElementAtOrDefault(JobIndex)?.Value is { } id && _rules.TryGetValue(id, out var r) ? r : BreakRule.None;
            int? overrideMinutes = HasBreakOverride && int.TryParse(BreakText.Trim(), CultureInfo.InvariantCulture, out var o) ? o : HasBreakOverride ? 0 : null;
            var unpaid = rule.UnpaidBreakFor(scheduled.Value, overrideMinutes);
            var paid = rule.PaidMinutesFor(scheduled.Value, overrideMinutes);
            var text = unpaid > 0
                ? $"{HoursFormat.Of(paid)} paid ({HoursFormat.Of(scheduled.Value)} shift − {unpaid} min unpaid break)"
                : $"{HoursFormat.Of(paid)} paid (no unpaid break)";
            return rule.PaidMinutes > 0 ? $"{text}. Includes a paid {rule.PaidMinutes} min break." : text + ".";
        }
    }

    private int? CurrentScheduledMinutes()
    {
        if (UsesHours)
        {
            return decimal.TryParse(HoursText.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var h) && h > 0 ? (int)Math.Round(h * 60) : null;
        }
        var span = (int)(TimeOnly.FromTimeSpan(EndTime) - TimeOnly.FromTimeSpan(StartTime)).TotalMinutes;
        return span == 0 ? 24 * 60 : span;
    }
    [ObservableProperty] public partial string Note { get; set; } = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => ErrorMessage is not null;
    public bool UsesHours => ModeIndex == 0;
    public bool UsesTimes => ModeIndex == 1;
    public bool HasSeveralJobs => JobOptions.Count > 1;
    public string Title => _editingId is null ? "Add shift" : "Edit shift";

    public async Task LoadForCreateAsync(DateOnly? date = null)
    {
        _editingId = null;
        await LoadJobsAsync();
        Date = new DateTimeOffset((date ?? DateOnly.FromDateTime(time.GetLocalNow().Date)).ToDateTime(TimeOnly.MinValue));
    }

    public async Task LoadForEditAsync(Shift shift)
    {
        _editingId = shift.Id;
        await LoadJobsAsync();
        JobIndex = Math.Max(0, JobOptions.ToList().FindIndex(o => o.Value == shift.JobId));
        Date = new DateTimeOffset(shift.Date.ToDateTime(TimeOnly.MinValue));
        if (shift.StartTime is { } s && shift.EndTime is { } e)
        {
            ModeIndex = 1;
            StartTime = s.ToTimeSpan();
            EndTime = e.ToTimeSpan();
        }
        HoursText = (shift.ScheduledMinutes / 60m).ToString("0.##", CultureInfo.InvariantCulture);
        HasBreakOverride = shift.BreakOverrideMinutes is not null;
        BreakText = shift.BreakOverrideMinutes?.ToString(CultureInfo.InvariantCulture) ?? "";
        IsWorked = shift.Status == ShiftStatus.Worked;
        Note = shift.Note ?? "";
    }

    private async Task LoadJobsAsync()
    {
        JobOptions.Clear();
        _rules.Clear();
        foreach (var job in await income.GetJobsAsync())
        {
            JobOptions.Add(new(job.Name, job.Id));
            _rules[job.Id] = BreakRule.For(job);
        }
        OnPropertyChanged(nameof(HasSeveralJobs));
        OnPropertyChanged(nameof(PaidPreview));
        ErrorMessage = null;
    }

    public async Task<bool> SaveAsync()
    {
        ErrorMessage = null;
        if (JobOptions.ElementAtOrDefault(JobIndex)?.Value is not { } jobId)
        {
            ErrorMessage = "Add a job first.";
            return false;
        }
        if (Date is not { } date)
        {
            ErrorMessage = "Choose a date.";
            return false;
        }

        int? minutes = null;
        if (UsesHours)
        {
            if (!decimal.TryParse(HoursText.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var hours) || hours <= 0)
            {
                ErrorMessage = "Enter the shift length in hours, like 6.";
                return false;
            }
            minutes = (int)Math.Round(hours * 60);
        }

        int? breakOverride = null;
        if (HasBreakOverride)
        {
            if (!int.TryParse(string.IsNullOrWhiteSpace(BreakText) ? "0" : BreakText.Trim(), CultureInfo.InvariantCulture, out var b))
            {
                ErrorMessage = "Enter this shift's unpaid break in minutes, like 30 (or 0 for none).";
                return false;
            }
            breakOverride = b;
        }

        var input = new ShiftInput(
            jobId,
            DateOnly.FromDateTime(date.Date),
            minutes,
            UsesTimes ? TimeOnly.FromTimeSpan(StartTime) : null,
            UsesTimes ? TimeOnly.FromTimeSpan(EndTime) : null,
            breakOverride,
            IsWorked ? ShiftStatus.Worked : ShiftStatus.Scheduled,
            Note);
        try
        {
            if (_editingId is { } id)
            {
                await income.UpdateShiftAsync(id, input);
            }
            else
            {
                await income.AddShiftAsync(input);
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
            logger.LogError(ex, "Failed to save shift");
            ErrorMessage = "Something went wrong while saving this shift. Your existing data has not been changed.";
            return false;
        }
    }
}

/// <summary>One line of the paste preview.</summary>
public sealed record PastePreviewRow(string DateText, string HoursText, string Detail);

/// <summary>Reads pasted schedule text live and saves the shifts it finds.</summary>
public sealed partial class PasteScheduleViewModel(IncomeService income, TimeProvider time, ILogger<PasteScheduleViewModel> logger)
    : ObservableObject
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");
    private ScheduleParseResult _result = new([], [], []);
    private readonly Dictionary<int, BreakRule> _rules = [];

    public ObservableCollection<FilterOption<int?>> JobOptions { get; } = [];
    public ObservableCollection<PastePreviewRow> Preview { get; } = [];
    public ObservableCollection<string> Problems { get; } = [];

    [ObservableProperty] public partial int JobIndex { get; set; }
    [ObservableProperty] public partial string Text { get; set; } = "";

    /// <summary>How the selected job's break rule will be applied, shown under the preview.</summary>
    [ObservableProperty] public partial string BreakRuleText { get; set; } = "";

    private BreakRule SelectedRule =>
        JobOptions.ElementAtOrDefault(JobIndex)?.Value is { } id && _rules.TryGetValue(id, out var r) ? r : BreakRule.None;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty] public partial string SummaryText { get; set; } = "Paste your schedule above.";
    [ObservableProperty] public partial bool HasShifts { get; set; }
    [ObservableProperty] public partial bool HasProblems { get; set; }
    [ObservableProperty] public partial string SaveText { get; set; } = "Add shifts";

    public bool HasError => ErrorMessage is not null;
    public bool HasSeveralJobs => JobOptions.Count > 1;

    public async Task LoadAsync()
    {
        JobOptions.Clear();
        _rules.Clear();
        foreach (var job in await income.GetJobsAsync())
        {
            JobOptions.Add(new(job.Name, job.Id));
            _rules[job.Id] = BreakRule.For(job);
        }
        OnPropertyChanged(nameof(HasSeveralJobs));
        Reparse();
    }

    partial void OnTextChanged(string value) => Reparse();
    partial void OnJobIndexChanged(int value) => Reparse();

    private void Reparse()
    {
        var today = DateOnly.FromDateTime(time.GetLocalNow().Date);
        _result = ScheduleParser.Parse(Text, today);
        var rule = SelectedRule;
        BreakRuleText = rule.UnpaidMinutes > 0
            ? $"Hours shown are paid time: {rule.UnpaidMinutes} min unpaid break taken off shifts of {HoursFormat.Of(rule.UnpaidAfterMinutes)} or more (change this in the job's settings)."
            : "Hours shown are paid time. This job has no unpaid break.";

        Preview.Clear();
        foreach (var s in _result.Shifts.OrderBy(s => s.Date))
        {
            var unpaid = rule.UnpaidBreakFor(s.ScheduledMinutes, s.BreakMinutes);
            var parts = new List<string>();
            if (s.Start is { } st && s.End is { } en)
            {
                parts.Add($"{st.ToString("h:mm tt", Culture)} – {en.ToString("h:mm tt", Culture)}");
            }
            if (unpaid > 0)
            {
                parts.Add($"{HoursFormat.Of(s.ScheduledMinutes)} shift − {unpaid} min unpaid break");
            }
            Preview.Add(new PastePreviewRow(s.Date.ToString("ddd, MMM d", Culture),
                HoursFormat.Of(rule.PaidMinutesFor(s.ScheduledMinutes, s.BreakMinutes)), string.Join("  ·  ", parts)));
        }
        Problems.Clear();
        foreach (var line in _result.Unreadable)
        {
            Problems.Add(line);
        }

        HasShifts = _result.Shifts.Count > 0;
        HasProblems = Problems.Count > 0;
        var total = _result.Shifts.Sum(s => rule.PaidMinutesFor(s.ScheduledMinutes, s.BreakMinutes));
        SummaryText = _result.Shifts.Count == 0
            ? (string.IsNullOrWhiteSpace(Text) ? "Paste your schedule above." : "No shifts found yet.")
            : $"{_result.Shifts.Count} {(_result.Shifts.Count == 1 ? "shift" : "shifts")}, {HoursFormat.Of(total)} paid in total" +
              (_result.DaysOff.Count > 0 ? $"  ·  {_result.DaysOff.Count} day(s) off skipped" : "");
        SaveText = _result.Shifts.Count == 0 ? "Add shifts" : $"Add {_result.Shifts.Count} {(_result.Shifts.Count == 1 ? "shift" : "shifts")}";
    }

    public async Task<bool> SaveAsync()
    {
        ErrorMessage = null;
        if (JobOptions.ElementAtOrDefault(JobIndex)?.Value is not { } jobId)
        {
            ErrorMessage = "Add a job first.";
            return false;
        }
        if (_result.Shifts.Count == 0)
        {
            ErrorMessage = "There are no shifts to add yet.";
            return false;
        }
        try
        {
            await income.SavePastedShiftsAsync(jobId, _result.Shifts);
            return true;
        }
        catch (LedgerValidationException ex)
        {
            ErrorMessage = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save pasted shifts");
            ErrorMessage = "Something went wrong while saving these shifts. Your existing data has not been changed.";
            return false;
        }
    }
}

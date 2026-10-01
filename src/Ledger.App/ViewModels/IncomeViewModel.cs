using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ledger.Core;
using Ledger.Core.Models;
using Ledger.Core.Payroll;
using Ledger.Core.Services;
using Microsoft.Extensions.Logging;

namespace Ledger.App.ViewModels;

/// <summary>A summary card: estimated income over one window.</summary>
public sealed record IncomeWindowViewModel(string Title, string NetText, string GrossText, string Caption);

/// <summary>One predicted paycheck in the list.</summary>
public sealed class PaycheckRowViewModel(PredictedPaycheck paycheck, DateOnly today)
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");

    public PredictedPaycheck Paycheck { get; } = paycheck;

    public string PayDateText => Paycheck.Period.PayDate.ToString("ddd, MMM d", Culture);
    public string JobName => Paycheck.JobName;

    public string PeriodText =>
        $"Worked {Paycheck.Period.Start.ToString("MMM d", Culture)} – {Paycheck.Period.End.ToString("MMM d", Culture)}  ·  {HoursFormat.Of(Paycheck.TotalMinutes)}" +
        (Paycheck.OvertimeMinutes > 0 ? $" incl. {HoursFormat.Of(Paycheck.OvertimeMinutes)} overtime" : "");

    public string NetText => Money.Format(Paycheck.NetCents);
    public string GrossText => $"{Money.Format(Paycheck.GrossCents)} gross";

    public bool IsRecorded => Paycheck.Recorded is not null;
    public string RecordedText => Paycheck.Recorded is { } r
        ? $"Recorded {Money.Format(r.ActualCents)} ({Money.Format(r.ActualCents - Paycheck.NetCents, MoneySign.Always)} vs estimate)"
        : "";

    /// <summary>"Paystub matches" or "Paystub: 2 lines differ", once a paystub has been entered.</summary>
    public bool HasPaystub => Paycheck.Paystub is not null;
    public string PaystubText { get; } = paycheck.Paystub is not { } stub ? ""
        : PaystubComparison.Compare(paycheck, stub) is var report && report.AllMatch ? "Paystub matches"
        : $"Paystub: {report.Lines.Count(l => l.Status == StubLineStatus.Differs)} line(s) differ";

    public bool IsPaydayAssumed => !Paycheck.IsPaydayConfirmed && !IsRecorded;
    public bool IsDue => !IsRecorded && Paycheck.Period.PayDate <= today;
    public bool IsEstimate => !IsRecorded;

    public string MoreOptionsName => $"Paycheck details for {PayDateText}";

    public override string ToString() =>
        $"{PayDateText}, {JobName}, estimated {NetText} take-home, {GrossText}, {PeriodText}" +
        (IsRecorded ? $", {RecordedText}" : "") + (IsPaydayAssumed ? ", payday not confirmed" : "");
}

/// <summary>One shift in the schedule list.</summary>
public sealed class ShiftRowViewModel(Shift shift, string jobName, bool showJob, BreakRule rule)
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");

    public Shift Shift { get; } = shift;
    public string DateText => Shift.Date.ToString("ddd, MMM d", Culture);

    /// <summary>Paid time after the unpaid break.</summary>
    public int PaidMinutes => rule.PaidMinutesFor(Shift.ScheduledMinutes, Shift.BreakOverrideMinutes);
    public string HoursText => HoursFormat.Of(PaidMinutes);

    public string DetailText
    {
        get
        {
            var parts = new List<string>();
            if (showJob) parts.Add(jobName);
            if (Shift.StartTime is { } s && Shift.EndTime is { } e)
                parts.Add($"{s.ToString("h:mm tt", Culture)} – {e.ToString("h:mm tt", Culture)}");
            var unpaid = rule.UnpaidBreakFor(Shift.ScheduledMinutes, Shift.BreakOverrideMinutes);
            if (unpaid > 0)
                parts.Add($"{HoursFormat.Of(Shift.ScheduledMinutes)} shift − {unpaid} min unpaid break");
            if (Shift.Note is { } n) parts.Add(n);
            return string.Join("  ·  ", parts);
        }
    }

    public bool IsWorked => Shift.Status == ShiftStatus.Worked;
    public string StatusText => IsWorked ? "Worked" : "Scheduled";
    public string MarkText => IsWorked ? "Mark as scheduled" : "Mark as worked";
    public string MoreOptionsName => $"More options for the shift on {DateText}";

    public override string ToString() => $"{DateText}, {HoursText}, {StatusText}" + (DetailText.Length > 0 ? $", {DetailText}" : "");
}

/// <summary>A job's settings in one glance.</summary>
public sealed class JobRowViewModel(Job job, DateOnly today)
{
    public Job Job { get; } = job;
    public string Name => Job.Name;

    public string PayText
    {
        get
        {
            var rate = Job.Rates.Where(r => r.EffectiveDate <= today).MaxBy(r => r.EffectiveDate) ?? Job.Rates.MinBy(r => r.EffectiveDate);
            var frequency = Job.PayFrequency switch
            {
                PayFrequency.Weekly => "paid weekly",
                PayFrequency.Biweekly => "paid every two weeks",
                PayFrequency.Semimonthly => "paid twice a month",
                _ => "paid monthly",
            };
            return $"{(rate is null ? "No rate" : Money.Format(rate.HourlyRateCents) + "/hour")}  ·  {frequency}" +
                   (Job.IsPaydayConfirmed ? "" : "  ·  payday not confirmed");
        }
    }

    public string BreakText => Job.UnpaidBreakMinutes > 0
        ? $"{Job.UnpaidBreakMinutes} min unpaid break on shifts of {HoursFormat.Of(Job.UnpaidBreakAfterMinutes)} or more" +
          (Job.PaidBreakMinutes > 0 ? $"  ·  {Job.PaidBreakMinutes} min paid break" : "")
        : Job.PaidBreakMinutes > 0 ? $"{Job.PaidBreakMinutes} min paid break  ·  no unpaid break" : "No breaks set";

    public string TaxText =>
        (Job.WorkLocationName ?? "Work location not set") +
        $"  ·  {Job.WorkNonResidentEitBasisPoints / 100m:0.##}% non-resident tax  ·  {Money.FormatCompact(Job.WorkLstAnnualCents)} LST" +
        (Job.IsLstExempt ? " (exempt)" : "");

    public string EditName => $"Edit {Name}";
}

/// <summary>Shifts in one Sunday-to-Saturday week.</summary>
public sealed class ShiftWeekGroup(string header, IEnumerable<ShiftRowViewModel> rows) : List<ShiftRowViewModel>(rows)
{
    public string Header { get; } = header;
}

public sealed partial class IncomeViewModel(
    IncomeService income,
    IncomeForecastService forecast,
    TimeProvider time,
    ILogger<IncomeViewModel> logger) : ObservableObject
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");

    /// <summary>How far back and ahead the lists reach.</summary>
    private const int DaysBack = 28, DaysAhead = 70;

    public ObservableCollection<IncomeWindowViewModel> Windows { get; } = [];
    public ObservableCollection<PaycheckRowViewModel> Paychecks { get; } = [];
    public ObservableCollection<ShiftWeekGroup> ShiftWeeks { get; } = [];
    public ObservableCollection<JobRowViewModel> Jobs { get; } = [];

    /// <summary>0 = count by payday, 1 = count by work date.</summary>
    [ObservableProperty] public partial int CountByIndex { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowNoJobs), nameof(ShowContent), nameof(ShowNoShifts))]
    public partial bool IsLoaded { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowNoJobs), nameof(ShowContent), nameof(ShowNoShifts))]
    public partial bool HasJobs { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowNoShifts))]
    public partial bool HasShifts { get; set; }

    [ObservableProperty] public partial bool HasPaychecks { get; set; }
    [ObservableProperty] public partial string CountByExplanation { get; set; } = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => ErrorMessage is not null;
    public bool ShowNoJobs => IsLoaded && !HasJobs;
    public bool ShowContent => IsLoaded && HasJobs;
    public bool ShowNoShifts => IsLoaded && HasJobs && !HasShifts;

    private DateOnly Today => DateOnly.FromDateTime(time.GetLocalNow().Date);

    partial void OnCountByIndexChanged(int value) => _ = LoadAsync();

    [RelayCommand]
    public async Task LoadAsync()
    {
        ErrorMessage = null;
        try
        {
            var today = Today;
            var jobs = await income.GetJobsAsync();
            Jobs.Clear();
            foreach (var j in jobs) Jobs.Add(new JobRowViewModel(j, today));
            HasJobs = jobs.Count > 0;

            var byWorkDate = CountByIndex == 1;
            CountByExplanation = byWorkDate
                ? "Counting what you earn on the days you work, whenever it's paid."
                : "Counting paychecks on the day they arrive. Best for planning spending.";

            Windows.Clear();
            foreach (var w in await forecast.GetWindowsAsync(today, byWorkDate))
            {
                var caption = byWorkDate
                    ? $"Earned {w.From.ToString("MMM d", Culture)} – {w.To.ToString("MMM d", Culture)}"
                    : w.PaycheckCount switch { 0 => "No paydays", 1 => "1 payday", var n => $"{n} paydays" };
                Windows.Add(new IncomeWindowViewModel(w.Title, Money.Format(w.NetCents), $"{Money.Format(w.GrossCents)} gross", caption));
            }

            Paychecks.Clear();
            // Recent paychecks, plus any older one still waiting to be recorded, however long ago.
            var paychecks = await forecast.GetPaychecksAsync(today.AddDays(-ForecastService.PaycheckLookBackDays), today.AddDays(DaysAhead));
            foreach (var p in paychecks.Where(p => p.Recorded is null || p.Period.PayDate >= today.AddDays(-DaysBack)))
            {
                Paychecks.Add(new PaycheckRowViewModel(p, today));
            }
            HasPaychecks = Paychecks.Count > 0;

            var byId = jobs.ToDictionary(j => j.Id);
            var showJob = jobs.Count > 1;
            var shifts = await income.GetShiftsAsync(today.AddDays(-DaysBack), today.AddDays(DaysAhead));
            ShiftWeeks.Clear();
            foreach (var week in shifts.Where(s => byId.ContainsKey(s.JobId)).GroupBy(s => PayPeriodCalendar.WorkweekStart(s.Date, DayOfWeek.Sunday)))
            {
                var rows = week.Select(s => new ShiftRowViewModel(s, byId[s.JobId].Name, showJob, BreakRule.For(byId[s.JobId]))).ToList();
                ShiftWeeks.Add(new ShiftWeekGroup(
                    $"Week of {week.Key.ToString("MMM d", Culture)}  ·  {HoursFormat.Of(rows.Sum(r => r.PaidMinutes))} paid",
                    rows));
            }
            HasShifts = ShiftWeeks.Count > 0;

            IsLoaded = true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load income");
            ErrorMessage = "Something went wrong while loading your income. Your existing data has not been changed.";
        }
    }

    public async Task ToggleWorkedAsync(ShiftRowViewModel row)
    {
        var s = row.Shift;
        // Pass the stored length and break through: changing status must never change the hours.
        await RunAsync(() => income.UpdateShiftAsync(s.Id, new ShiftInput(
            s.JobId, s.Date, s.ScheduledMinutes, s.StartTime, s.EndTime, s.BreakOverrideMinutes,
            row.IsWorked ? ShiftStatus.Scheduled : ShiftStatus.Worked, s.Note)));
    }

    public Task DeleteShiftAsync(ShiftRowViewModel row) => RunAsync(() => income.DeleteShiftAsync(row.Shift.Id));

    public Task RecordPaycheckAsync(PredictedPaycheck paycheck, int transactionId) =>
        RunAsync(() => forecast.RecordPaycheckAsync(paycheck.JobId, paycheck.Period, paycheck.NetCents, transactionId));

    private async Task RunAsync(Func<Task> action)
    {
        ErrorMessage = null;
        try
        {
            await action();
        }
        catch (LedgerValidationException ex)
        {
            ErrorMessage = ex.Message;
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Income action failed");
            ErrorMessage = "Something went wrong. Your existing data has not been changed.";
            return;
        }
        await LoadAsync();
    }
}

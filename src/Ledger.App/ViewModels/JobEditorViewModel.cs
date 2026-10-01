using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Ledger.Core;
using Ledger.Core.Models;
using Ledger.Core.Payroll;
using Ledger.Core.Services;
using Microsoft.Extensions.Logging;

namespace Ledger.App.ViewModels;

/// <summary>Form state for adding or editing a job.</summary>
public sealed partial class JobEditorViewModel(
    IncomeService income,
    AccountService accounts,
    CategoryService categories,
    DeductionService deductionService,
    TimeProvider time,
    ILogger<JobEditorViewModel> logger) : ObservableObject
{
    /// <summary>Deductions taken from each paycheck (benefits, 401(k), dues).</summary>
    public System.Collections.ObjectModel.ObservableCollection<DeductionRowViewModel> PaycheckDeductions { get; } = [];

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void AddDeduction() => PaycheckDeductions.Add(new DeductionRowViewModel(DateOnly.FromDateTime(time.GetLocalNow().Date)));

    public void RemoveDeduction(DeductionRowViewModel row) => PaycheckDeductions.Remove(row);

    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");
    private int? _editingId;

    public IReadOnlyList<FilterOption<PayFrequency?>> FrequencyOptions { get; } =
    [
        new("Weekly", PayFrequency.Weekly),
        new("Every two weeks", PayFrequency.Biweekly),
        new("Twice a month (1st–15th, 16th–end)", PayFrequency.Semimonthly),
        new("Monthly", PayFrequency.Monthly),
    ];

    public IReadOnlyList<FilterOption<FilingStatus?>> FilingOptions { get; } =
    [
        new("Single or married filing separately", FilingStatus.Single),
        new("Married filing jointly", FilingStatus.MarriedFilingJointly),
        new("Head of household", FilingStatus.HeadOfHousehold),
    ];

    public IReadOnlyList<string> WeekdayOptions { get; } =
        Enumerable.Range(0, 7).Select(d => Culture.DateTimeFormat.GetDayName((DayOfWeek)d)).ToList();

    public ObservableCollection<FilterOption<int?>> AccountOptions { get; } = [];
    public ObservableCollection<FilterOption<int?>> CategoryOptions { get; } = [];

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string RateText { get; set; } = "";
    [ObservableProperty] public partial DateTimeOffset? RateEffective { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(NextPaydayText), nameof(ShowsAnchor))]
    public partial int FrequencyIndex { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(NextPaydayText))]
    public partial DateTimeOffset? Anchor { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(NextPaydayText))]
    public partial string PaydayOffsetText { get; set; } = "6";

    [ObservableProperty] public partial bool IsPaydayConfirmed { get; set; }
    [ObservableProperty] public partial int WorkweekStartIndex { get; set; }
    [ObservableProperty] public partial bool IsOvertimeEligible { get; set; } = true;
    [ObservableProperty] public partial int AccountIndex { get; set; }
    [ObservableProperty] public partial int CategoryIndex { get; set; }
    [ObservableProperty] public partial int FilingIndex { get; set; }
    [ObservableProperty] public partial bool MultipleJobs { get; set; }
    [ObservableProperty] public partial string DependentsText { get; set; } = "";
    [ObservableProperty] public partial string OtherIncomeText { get; set; } = "";
    [ObservableProperty] public partial string DeductionsText { get; set; } = "";
    [ObservableProperty] public partial string ExtraWithholdingText { get; set; } = "";
    [ObservableProperty] public partial string WorkLocation { get; set; } = "";
    [ObservableProperty] public partial string WorkPsd { get; set; } = "";
    [ObservableProperty] public partial string WorkEitText { get; set; } = "";
    [ObservableProperty] public partial string WorkLstText { get; set; } = "";
    [ObservableProperty] public partial bool IsLstExempt { get; set; }

    // Break rule. Defaults match the user's employer: 15 min paid always; 30 min unpaid from 5 hours.
    [ObservableProperty, NotifyPropertyChangedFor(nameof(BreakExample))]
    public partial string UnpaidBreakText { get; set; } = "30";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(BreakExample))]
    public partial string UnpaidAfterHoursText { get; set; } = "5";

    [ObservableProperty] public partial string PaidBreakText { get; set; } = "15";

    /// <summary>A worked example so the rule is easy to check.</summary>
    public string BreakExample =>
        TryBreakRule(out var unpaid, out var after, out _) && unpaid > 0
            ? $"For example, a 6 h shift pays {HoursFormat.Of(Math.Max(0, 360 - (360 >= after ? unpaid : 0)))} and a 4 h shift pays {HoursFormat.Of(Math.Max(0, 240 - (240 >= after ? unpaid : 0)))}."
            : "No unpaid break: every scheduled hour is paid.";

    private bool TryBreakRule(out int unpaidMinutes, out int afterMinutes, out int paidMinutes)
    {
        afterMinutes = 0;
        paidMinutes = 0;
        var ok = int.TryParse(Blank0(UnpaidBreakText), CultureInfo.InvariantCulture, out unpaidMinutes)
                 & int.TryParse(Blank0(PaidBreakText), CultureInfo.InvariantCulture, out paidMinutes)
                 & decimal.TryParse(Blank0(UnpaidAfterHoursText), NumberStyles.Number, CultureInfo.InvariantCulture, out var afterHours);
        afterMinutes = (int)Math.Round(afterHours * 60);
        return ok;

        static string Blank0(string s) => string.IsNullOrWhiteSpace(s) ? "0" : s.Trim();
    }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty] public partial bool IsSaving { get; set; }

    public bool HasError => ErrorMessage is not null;
    public bool IsNew => _editingId is null;
    public bool IsExisting => !IsNew;
    public string Title => IsNew ? "Add job" : "Edit job";
    public string HomeTaxText { get; private set; } = "";

    private PayFrequency Frequency => FrequencyOptions[Math.Clamp(FrequencyIndex, 0, FrequencyOptions.Count - 1)].Value!.Value;

    /// <summary>Weekly and biweekly periods need a start date; the others follow the calendar.</summary>
    public bool ShowsAnchor => Frequency is PayFrequency.Weekly or PayFrequency.Biweekly;

    /// <summary>A live example so the user can check the payday settings make sense.</summary>
    public string NextPaydayText
    {
        get
        {
            if (!int.TryParse(PaydayOffsetText, CultureInfo.InvariantCulture, out var offset) || offset is < 0 or > 31)
            {
                return "Enter a number of days from 0 to 31.";
            }
            var today = DateOnly.FromDateTime(time.GetLocalNow().Date);
            var anchor = Anchor is { } a ? DateOnly.FromDateTime(a.Date) : today;
            var period = PayPeriodCalendar.PeriodContaining(today, Frequency, anchor, offset);
            return $"This period runs {period.Start.ToString("ddd, MMM d", Culture)} – {period.End.ToString("ddd, MMM d", Culture)} " +
                   $"and would be paid on {period.PayDate.ToString("dddd, MMM d", Culture)}.";
        }
    }

    public async Task LoadForCreateAsync()
    {
        _editingId = null;
        await LoadListsAsync();
        var today = DateOnly.FromDateTime(time.GetLocalNow().Date);
        FrequencyIndex = 0;
        WorkweekStartIndex = (int)DayOfWeek.Sunday;
        Anchor = ToOffset(PayPeriodCalendar.WorkweekStart(today, DayOfWeek.Sunday));
        PaydayOffsetText = "6";  // Saturday period end → the following Friday
        RateEffective = ToOffset(today.AddYears(-1));
        CategoryIndex = Math.Max(0, CategoryOptions.ToList().FindIndex(o => o.Label.EndsWith("Paycheck", StringComparison.Ordinal)));
    }

    public async Task LoadForEditAsync(int jobId)
    {
        var job = await income.GetJobAsync(jobId);
        _editingId = job.Id;
        await LoadListsAsync();
        var today = DateOnly.FromDateTime(time.GetLocalNow().Date);
        var current = job.Rates.Where(r => r.EffectiveDate <= today).MaxBy(r => r.EffectiveDate) ?? job.Rates.MinBy(r => r.EffectiveDate);

        Name = job.Name;
        RateText = current is null ? "" : Money.FormatForEditing(current.HourlyRateCents);
        RateEffective = ToOffset(today);
        FrequencyIndex = FrequencyOptions.ToList().FindIndex(o => o.Value == job.PayFrequency);
        Anchor = ToOffset(job.PayPeriodAnchor);
        PaydayOffsetText = job.PaydayOffsetDays.ToString(CultureInfo.InvariantCulture);
        IsPaydayConfirmed = job.IsPaydayConfirmed;
        WorkweekStartIndex = (int)job.WorkweekStart;
        IsOvertimeEligible = job.IsOvertimeEligible;
        AccountIndex = Math.Max(0, AccountOptions.ToList().FindIndex(o => o.Value == job.DepositAccountId));
        CategoryIndex = Math.Max(0, CategoryOptions.ToList().FindIndex(o => o.Value == job.IncomeCategoryId));
        FilingIndex = FilingOptions.ToList().FindIndex(o => o.Value == job.FilingStatus);
        MultipleJobs = job.W4MultipleJobs;
        DependentsText = AmountOrBlank(job.W4DependentsCreditCents);
        OtherIncomeText = AmountOrBlank(job.W4OtherIncomeCents);
        DeductionsText = AmountOrBlank(job.W4DeductionsCents);
        ExtraWithholdingText = AmountOrBlank(job.W4ExtraWithholdingCents);
        WorkLocation = job.WorkLocationName ?? "";
        WorkPsd = job.WorkPsdCode ?? "";
        WorkEitText = job.WorkNonResidentEitBasisPoints == 0 ? "" : (job.WorkNonResidentEitBasisPoints / 100m).ToString("0.###", CultureInfo.InvariantCulture);
        WorkLstText = AmountOrBlank(job.WorkLstAnnualCents);
        IsLstExempt = job.IsLstExempt;
        UnpaidBreakText = job.UnpaidBreakMinutes.ToString(CultureInfo.InvariantCulture);
        UnpaidAfterHoursText = (job.UnpaidBreakAfterMinutes / 60m).ToString("0.##", CultureInfo.InvariantCulture);
        PaidBreakText = job.PaidBreakMinutes.ToString(CultureInfo.InvariantCulture);

        PaycheckDeductions.Clear();
        foreach (var d in await deductionService.GetCurrentAsync(job.Id, today))
        {
            PaycheckDeductions.Add(new DeductionRowViewModel(d));
        }
    }

    private async Task LoadListsAsync()
    {
        AccountOptions.Clear();
        AccountOptions.Add(new("Not set", null));
        foreach (var a in await accounts.GetAccountBalancesAsync())
        {
            AccountOptions.Add(new(a.Account.Name, a.Account.Id));
        }

        CategoryOptions.Clear();
        CategoryOptions.Add(new("No category", null));
        foreach (var node in (await categories.GetTreeAsync()).Where(n => n.Category.CategoryType == CategoryType.Income))
        {
            CategoryOptions.Add(new(node.Category.Name, node.Category.Id));
            foreach (var child in node.Children)
            {
                CategoryOptions.Add(new($"{node.Category.Name} › {child.Name}", child.Id));
            }
        }

        var home = await income.GetHomeTaxSettingsAsync();
        HomeTaxText = home switch
        {
            { InPennsylvania: false } => "Settings says you live and work outside Pennsylvania, so these local taxes aren't used.",
            { IsSet: false } => "You haven't entered where you live yet (Settings → Paycheck taxes), so only this work rate is used for now. " +
                                "Whichever of the two is higher is withheld.",
            _ => $"Your home rate is {home.ResidentEitBasisPoints / 100m:0.##}% ({home.LocationName}). " +
                 "Whichever of the two is higher is withheld. You can change your home rate in Settings.",
        };
        OnPropertyChanged(nameof(HomeTaxText));
        ErrorMessage = null;
    }

    public async Task<bool> SaveAsync()
    {
        ErrorMessage = null;
        var deductionInputs = new List<DeductionInput>();
        foreach (var row in PaycheckDeductions)
        {
            var (parsed, problem) = row.ToInput();
            if (problem is not null)
            {
                ErrorMessage = problem;
                return false;
            }
            deductionInputs.Add(parsed!);
        }
        if (!Money.TryParse(RateText, out var rate) || rate <= 0)
        {
            ErrorMessage = "Enter your hourly rate, like 13.50.";
            return false;
        }
        if (!int.TryParse(PaydayOffsetText, CultureInfo.InvariantCulture, out var offset))
        {
            ErrorMessage = "Enter how many days after the pay period ends you get paid.";
            return false;
        }
        if (!TryOptionalMoney(DependentsText, out var dependents) || !TryOptionalMoney(OtherIncomeText, out var otherIncome)
            || !TryOptionalMoney(DeductionsText, out var deductions) || !TryOptionalMoney(ExtraWithholdingText, out var extra)
            || !TryOptionalMoney(WorkLstText, out var lst))
        {
            ErrorMessage = "Check the dollar amounts. They should look like 52 or 12.50.";
            return false;
        }
        if (!TryBreakRule(out var unpaidBreak, out var unpaidAfter, out var paidBreak))
        {
            ErrorMessage = "Enter breaks in minutes (like 30) and the shift length in hours (like 5).";
            return false;
        }
        var eitBps = 0;
        if (!string.IsNullOrWhiteSpace(WorkEitText))
        {
            if (!decimal.TryParse(WorkEitText.Trim().TrimEnd('%'), NumberStyles.Number, CultureInfo.InvariantCulture, out var pct))
            {
                ErrorMessage = "Enter the work location's tax rate as a percent, like 0.5.";
                return false;
            }
            eitBps = (int)Math.Round(pct * 100);
        }

        var today = DateOnly.FromDateTime(time.GetLocalNow().Date);
        var input = new JobInput(
            Name,
            rate,
            RateEffective is { } re ? DateOnly.FromDateTime(re.Date) : today,
            Frequency,
            Anchor is { } an ? DateOnly.FromDateTime(an.Date) : today,
            offset,
            IsPaydayConfirmed,
            (DayOfWeek)Math.Clamp(WorkweekStartIndex, 0, 6),
            IsOvertimeEligible,
            AccountOptions.ElementAtOrDefault(AccountIndex)?.Value,
            CategoryOptions.ElementAtOrDefault(CategoryIndex)?.Value,
            FilingOptions[Math.Clamp(FilingIndex, 0, FilingOptions.Count - 1)].Value!.Value,
            MultipleJobs,
            dependents, otherIncome, deductions, extra,
            WorkLocation, WorkPsd, eitBps, lst, IsLstExempt,
            unpaidBreak, unpaidAfter, paidBreak);

        IsSaving = true;
        try
        {
            if (_editingId is { } id)
            {
                await income.UpdateJobAsync(id, input);
                await deductionService.SaveAsync(id, deductionInputs, DateOnly.FromDateTime(time.GetLocalNow().Date));
            }
            else
            {
                var job = await income.CreateJobAsync(input);
                _editingId = job.Id; // a retry after a deduction problem then updates, never duplicates
                await deductionService.SaveAsync(job.Id, deductionInputs, DateOnly.FromDateTime(time.GetLocalNow().Date));
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
            logger.LogError(ex, "Failed to save job");
            ErrorMessage = "Something went wrong while saving this job. Your existing data has not been changed.";
            return false;
        }
        finally
        {
            IsSaving = false;
        }
    }

    private static bool TryOptionalMoney(string text, out long cents)
    {
        cents = 0;
        return string.IsNullOrWhiteSpace(text) || (Money.TryParse(text, out cents) && cents >= 0);
    }

    private static string AmountOrBlank(long cents) => cents == 0 ? "" : Money.FormatForEditing(cents);
    private static DateTimeOffset ToOffset(DateOnly d) => new(d.ToDateTime(TimeOnly.MinValue));
}

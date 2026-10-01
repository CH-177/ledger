namespace Ledger.Core.Models;

// Persisted as integers. Never renumber; only append.

public enum PayFrequency
{
    Weekly = 0,
    Biweekly = 1,
    Semimonthly = 2,
    Monthly = 3,
}

/// <summary>Federal filing status as chosen in Form W-4 Step 1(c).</summary>
public enum FilingStatus
{
    Single = 0,
    MarriedFilingJointly = 1,
    HeadOfHousehold = 2,
}

public enum ShiftStatus
{
    /// <summary>On the schedule; not yet confirmed as worked.</summary>
    Scheduled = 0,
    Worked = 1,
}

/// <summary>
/// A W-2 job whose paychecks Ledger predicts. Pay rates live in <see cref="JobRate"/> so a raise
/// never rewrites earlier estimates.
/// </summary>
public class Job : IAuditable
{
    public int Id { get; set; }
    public required string Name { get; set; }

    public PayFrequency PayFrequency { get; set; } = PayFrequency.Weekly;

    /// <summary>The first day of any real pay period; weekly and biweekly periods repeat from it.</summary>
    public DateOnly PayPeriodAnchor { get; set; }

    /// <summary>Days from the end of a pay period to payday (e.g. 6: Saturday end → Friday).</summary>
    public int PaydayOffsetDays { get; set; } = 6;

    /// <summary>False until the user confirms the payday from a real paystub.</summary>
    public bool IsPaydayConfirmed { get; set; }

    /// <summary>First day of the employer's fixed 7-day workweek, used for overtime.</summary>
    public DayOfWeek WorkweekStart { get; set; } = DayOfWeek.Sunday;

    /// <summary>Pay 1.5× for hours over 40 in a workweek (FLSA and PA Minimum Wage Act).</summary>
    public bool IsOvertimeEligible { get; set; } = true;

    // --- Break rule (employer policy) ---

    /// <summary>Unpaid meal break taken off shifts of at least <see cref="UnpaidBreakAfterMinutes"/>.</summary>
    public int UnpaidBreakMinutes { get; set; } = 30;

    /// <summary>Shift length at which the unpaid break applies (5 hours = 300).</summary>
    public int UnpaidBreakAfterMinutes { get; set; } = 300;

    /// <summary>Paid rest break per shift. Shown for information only: it doesn't change pay.</summary>
    public int PaidBreakMinutes { get; set; } = 15;

    public int? DepositAccountId { get; set; }
    public Account? DepositAccount { get; set; }

    public int? IncomeCategoryId { get; set; }
    public Category? IncomeCategory { get; set; }

    // --- Form W-4 (2020 or later) ---
    public FilingStatus FilingStatus { get; set; } = FilingStatus.Single;
    public bool W4MultipleJobs { get; set; }
    public long W4DependentsCreditCents { get; set; }
    public long W4OtherIncomeCents { get; set; }
    public long W4DeductionsCents { get; set; }
    public long W4ExtraWithholdingCents { get; set; }

    // --- Where the job is (local taxes are set by the work location) ---
    public string? WorkLocationName { get; set; }
    public string? WorkPsdCode { get; set; }

    /// <summary>Work municipality's non-resident earned income tax, in basis points (0.5% = 50).</summary>
    public int WorkNonResidentEitBasisPoints { get; set; }

    /// <summary>Work municipality's Local Services Tax per year, in cents (at most $52).</summary>
    public long WorkLstAnnualCents { get; set; }

    /// <summary>The user has filed an LST low-income exemption with this employer.</summary>
    public bool IsLstExempt { get; set; }

    public bool IsArchived { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public List<JobRate> Rates { get; set; } = [];
}

/// <summary>An hourly rate that applies from <see cref="EffectiveDate"/> until the next one.</summary>
public class JobRate
{
    public int Id { get; set; }
    public int JobId { get; set; }
    public Job? Job { get; set; }
    public DateOnly EffectiveDate { get; set; }
    public long HourlyRateCents { get; set; }
}

/// <summary>
/// A scheduled or worked shift. <see cref="ScheduledMinutes"/> is the shift's length on the clock;
/// paid time is that minus the unpaid break, which comes from the job's break rule unless
/// <see cref="BreakOverrideMinutes"/> is set. Start/end times are kept for display.
/// </summary>
public class Shift : IAuditable
{
    public int Id { get; set; }
    public int JobId { get; set; }
    public Job? Job { get; set; }
    public DateOnly Date { get; set; }

    /// <summary>Shift length before any unpaid break (e.g. "6hrs" on the schedule = 360).</summary>
    public int ScheduledMinutes { get; set; }

    public TimeOnly? StartTime { get; set; }
    public TimeOnly? EndTime { get; set; }

    /// <summary>An unpaid break for this shift only. Null uses the job's rule.</summary>
    public int? BreakOverrideMinutes { get; set; }

    public ShiftStatus Status { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Links a predicted paycheck to the real income transaction the user recorded for it,
/// so estimates can be compared with what was actually paid. Never created automatically.
/// </summary>
public class PaycheckRecord
{
    public int Id { get; set; }
    public int JobId { get; set; }
    public Job? Job { get; set; }
    public DateOnly PeriodStart { get; set; }
    public DateOnly PayDate { get; set; }
    public long EstimatedNetCents { get; set; }
    public int TransactionId { get; set; }
    public Transaction? Transaction { get; set; }
}

public enum DeductionKind
{
    /// <summary>401(k)/403(b): lowers federal income tax only (PA and local still tax it).</summary>
    PreTaxRetirement = 0,

    /// <summary>Section 125 health, dental, vision: lowers federal, Social Security, Medicare, PA and local tax.</summary>
    PreTaxHealth = 1,

    /// <summary>Roth 401(k), union dues, etc.: taken from pay after all taxes.</summary>
    AfterTax = 2,
}

/// <summary>
/// A deduction taken from each paycheck, from <see cref="StartDate"/> until <see cref="EndDate"/>.
/// Changing one closes it and starts a new one, like pay rates, so earlier estimates never change.
/// </summary>
public class JobDeduction
{
    public int Id { get; set; }
    public int JobId { get; set; }
    public Job? Job { get; set; }
    public required string Name { get; set; }
    public DeductionKind Kind { get; set; }

    /// <summary>A fixed amount per paycheck. Used when <see cref="PercentBasisPoints"/> is null.</summary>
    public long AmountCents { get; set; }

    /// <summary>A share of gross pay (500 = 5%), for retirement or after-tax savings. Null means fixed.</summary>
    public int? PercentBasisPoints { get; set; }

    /// <summary>Applies to pay periods ending on or after this date.</summary>
    public DateOnly StartDate { get; set; }

    /// <summary>Last date it applies (inclusive). Null means ongoing.</summary>
    public DateOnly? EndDate { get; set; }
}

/// <summary>
/// The numbers from a real paystub, entered by the user to check Ledger's estimate. Kept per pay
/// period, separate from recording the deposit. Only gross and net are required.
/// </summary>
public class Paystub : IAuditable
{
    public int Id { get; set; }
    public int JobId { get; set; }
    public Job? Job { get; set; }

    /// <summary>The pay period Ledger predicted this paycheck for (the link to the estimate).</summary>
    public DateOnly PeriodStart { get; set; }

    /// <summary>The pay period and payday printed on the stub, when entered.</summary>
    public DateOnly? StubPeriodStart { get; set; }
    public DateOnly? StubPeriodEnd { get; set; }
    public DateOnly PayDate { get; set; }

    public int? PaidMinutes { get; set; }
    public long GrossCents { get; set; }
    public long? FederalCents { get; set; }
    public long? SocialSecurityCents { get; set; }
    public long? MedicareCents { get; set; }
    public long? StateCents { get; set; }
    public long? StateUnemploymentCents { get; set; }
    public long? LocalEitCents { get; set; }
    public long? LocalServicesTaxCents { get; set; }
    public long NetCents { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

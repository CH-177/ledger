using System.Globalization;
using Ledger.Core.Data;
using Ledger.Core.Models;
using Ledger.Core.Payroll;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Core.Services;

/// <summary>Fields the user supplies for a job. The rate applies from <see cref="RateEffectiveDate"/>.</summary>
public sealed record JobInput(
    string Name,
    long HourlyRateCents,
    DateOnly RateEffectiveDate,
    PayFrequency PayFrequency,
    DateOnly PayPeriodAnchor,
    int PaydayOffsetDays,
    bool IsPaydayConfirmed,
    DayOfWeek WorkweekStart,
    bool IsOvertimeEligible,
    int? DepositAccountId,
    int? IncomeCategoryId,
    FilingStatus FilingStatus,
    bool W4MultipleJobs,
    long W4DependentsCreditCents,
    long W4OtherIncomeCents,
    long W4DeductionsCents,
    long W4ExtraWithholdingCents,
    string? WorkLocationName,
    string? WorkPsdCode,
    int WorkNonResidentEitBasisPoints,
    long WorkLstAnnualCents,
    bool IsLstExempt,
    int UnpaidBreakMinutes = 30,
    int UnpaidBreakAfterMinutes = 300,
    int PaidBreakMinutes = 15);

/// <summary>
/// A shift as entered: its scheduled length (minutes, or start/end times) and, optionally, an
/// unpaid break for this shift only. Without an override, the job's break rule applies.
/// </summary>
public sealed record ShiftInput(
    int JobId,
    DateOnly Date,
    int? ScheduledMinutes,
    TimeOnly? StartTime,
    TimeOnly? EndTime,
    int? BreakOverrideMinutes,
    ShiftStatus Status,
    string? Note);

/// <summary>
/// Home-location tax settings shared by all jobs. <paramref name="InPennsylvania"/> false means
/// paychecks are estimated with federal tax and FICA only: Ledger has no other state's tables.
/// </summary>
public sealed record HomeTaxSettings(string LocationName, string? PsdCode, int ResidentEitBasisPoints, bool InPennsylvania = true)
{
    /// <summary>The user has said where they live (a fresh install starts without a home location).</summary>
    public bool IsSet => LocationName.Length > 0;
}

/// <summary>Jobs, their rates, and shifts: the inputs to paycheck predictions.</summary>
public sealed class IncomeService(IDbContextFactory<LedgerDbContext> contextFactory, SettingsService settings)
{
    /// <summary>Hourly rates above this are almost certainly a typo ($1,000/hour).</summary>
    public const long MaxHourlyRateCents = 100_000;

    /// <summary>PA minimum wage in 2026. Rates below it are allowed (e.g. tipped roles) but flagged.</summary>
    public const long PennsylvaniaMinimumWageCents = 725;

    private const string HomeLocationKey = "Income.HomeLocationName";
    private const string HomePsdKey = "Income.HomePsdCode";
    private const string HomeEitKey = "Income.HomeResidentEitBasisPoints";
    private const string InPennsylvaniaKey = "Income.InPennsylvania";

    /// <summary>
    /// Nothing set: no home location and a 0% resident rate, so only the work location's rate applies
    /// until the user enters theirs. (Before 3.0.0 there was a built-in default town.)
    /// </summary>
    public static readonly HomeTaxSettings DefaultHome = new("", null, 0);

    // ---- Home tax settings --------------------------------------------------------------------

    public async Task<HomeTaxSettings> GetHomeTaxSettingsAsync(CancellationToken ct = default)
    {
        var name = await settings.GetAsync(HomeLocationKey, ct);
        var psd = await settings.GetAsync(HomePsdKey, ct);
        var eit = await settings.GetAsync(HomeEitKey, ct);
        var inPa = await settings.GetAsync(InPennsylvaniaKey, ct);
        return new HomeTaxSettings(
            name ?? DefaultHome.LocationName,
            string.IsNullOrEmpty(psd) ? DefaultHome.PsdCode : psd,
            int.TryParse(eit, CultureInfo.InvariantCulture, out var bps) ? bps : DefaultHome.ResidentEitBasisPoints,
            inPa != "false");
    }

    public async Task SetHomeTaxSettingsAsync(HomeTaxSettings home, CancellationToken ct = default)
    {
        if (home.ResidentEitBasisPoints is < 0 or > 1000)
        {
            throw new LedgerValidationException("Enter a local earned income tax rate between 0% and 10%.");
        }
        await settings.SetAsync(HomeLocationKey, home.LocationName.Trim(), ct);
        await settings.SetAsync(HomePsdKey, home.PsdCode?.Trim() ?? "", ct);
        await settings.SetAsync(HomeEitKey, home.ResidentEitBasisPoints.ToString(CultureInfo.InvariantCulture), ct);
        await settings.SetAsync(InPennsylvaniaKey, home.InPennsylvania ? "true" : "false", ct);
    }

    // ---- Jobs ---------------------------------------------------------------------------------

    public async Task<IReadOnlyList<Job>> GetJobsAsync(bool includeArchived = false, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.Jobs.AsNoTracking().Include(j => j.Rates)
            .Where(j => includeArchived || !j.IsArchived)
            .OrderBy(j => j.Name)
            .ToListAsync(ct);
    }

    public async Task<Job> GetJobAsync(int id, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.Jobs.AsNoTracking().Include(j => j.Rates).FirstOrDefaultAsync(j => j.Id == id, ct)
            ?? throw new LedgerValidationException("That job no longer exists.");
    }

    public async Task<Job> CreateJobAsync(JobInput input, CancellationToken ct = default)
    {
        Validate(input);
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await ValidateReferencesAsync(db, input, ct);
        var job = new Job { Name = input.Name.Trim() };
        Apply(job, input);
        job.Rates.Add(new JobRate { EffectiveDate = input.RateEffectiveDate, HourlyRateCents = input.HourlyRateCents });
        db.Jobs.Add(job);
        await db.SaveChangesAsync(ct);
        return job;
    }

    /// <summary>
    /// Updates a job. A changed rate is added as a new rate from its effective date, so estimates
    /// for earlier shifts keep the rate that applied then.
    /// </summary>
    public async Task UpdateJobAsync(int id, JobInput input, CancellationToken ct = default)
    {
        Validate(input);
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await ValidateReferencesAsync(db, input, ct);
        var job = await db.Jobs.Include(j => j.Rates).FirstOrDefaultAsync(j => j.Id == id, ct)
            ?? throw new LedgerValidationException("That job no longer exists.");
        job.Name = input.Name.Trim();
        Apply(job, input);

        var existing = job.Rates.FirstOrDefault(r => r.EffectiveDate == input.RateEffectiveDate);
        var current = job.Rates.Where(r => r.EffectiveDate <= input.RateEffectiveDate).MaxBy(r => r.EffectiveDate);
        if (existing is not null)
        {
            existing.HourlyRateCents = input.HourlyRateCents;
        }
        else if (current?.HourlyRateCents != input.HourlyRateCents)
        {
            job.Rates.Add(new JobRate { EffectiveDate = input.RateEffectiveDate, HourlyRateCents = input.HourlyRateCents });
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task SetJobArchivedAsync(int id, bool archived, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var job = await db.Jobs.FindAsync([id], ct) ?? throw new LedgerValidationException("That job no longer exists.");
        job.IsArchived = archived;
        await db.SaveChangesAsync(ct);
    }

    // ---- Shifts -------------------------------------------------------------------------------

    public async Task<IReadOnlyList<Shift>> GetShiftsAsync(DateOnly from, DateOnly to, int? jobId = null, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.Shifts.AsNoTracking()
            .Where(s => s.Date >= from && s.Date <= to && (jobId == null || s.JobId == jobId))
            .OrderBy(s => s.Date).ThenBy(s => s.StartTime)
            .ToListAsync(ct);
    }

    public async Task<Shift> AddShiftAsync(ShiftInput input, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var job = await RequireJobAsync(db, input.JobId, ct);
        var shift = new Shift();
        Apply(shift, input, BreakRule.For(job));
        db.Shifts.Add(shift);
        await db.SaveChangesAsync(ct);
        return shift;
    }

    public async Task UpdateShiftAsync(int id, ShiftInput input, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var job = await RequireJobAsync(db, input.JobId, ct);
        var shift = await db.Shifts.FindAsync([id], ct) ?? throw new LedgerValidationException("That shift no longer exists.");
        Apply(shift, input, BreakRule.For(job));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Shifts are plans, not financial history, so they can be removed outright.</summary>
    public async Task DeleteShiftAsync(int id, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var shift = await db.Shifts.FindAsync([id], ct) ?? throw new LedgerValidationException("That shift no longer exists.");
        db.Shifts.Remove(shift);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Saves pasted shifts. Scheduled shifts already on those dates for the job are replaced, so
    /// pasting an updated schedule never duplicates. Shifts marked worked are left alone.
    /// Returns (added, replaced).
    /// </summary>
    public async Task<(int Added, int Replaced)> SavePastedShiftsAsync(int jobId, IReadOnlyList<ParsedShift> parsed, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var rule = BreakRule.For(await RequireJobAsync(db, jobId, ct));
        var dates = parsed.Select(p => p.Date).Distinct().ToList();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var replaced = await db.Shifts
            .Where(s => s.JobId == jobId && s.Status == ShiftStatus.Scheduled && dates.Contains(s.Date))
            .ToListAsync(ct);
        db.Shifts.RemoveRange(replaced);

        foreach (var p in parsed)
        {
            var shift = new Shift();
            Apply(shift, new ShiftInput(jobId, p.Date, p.ScheduledMinutes, p.Start, p.End, p.BreakMinutes, ShiftStatus.Scheduled, null), rule);
            db.Shifts.Add(shift);
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (parsed.Count, replaced.Count);
    }

    /// <summary>
    /// A shift's scheduled length: explicit minutes when given, otherwise end − start
    /// (crossing midnight if needed).
    /// </summary>
    public static int ScheduledMinutesFor(ShiftInput input)
    {
        int minutes;
        if (input.ScheduledMinutes is { } explicitMinutes)
        {
            minutes = explicitMinutes;
        }
        else if (input.StartTime is { } start && input.EndTime is { } end)
        {
            var span = (int)(end - start).TotalMinutes;
            minutes = span == 0 ? 24 * 60 : span;
        }
        else
        {
            throw new LedgerValidationException("Enter the hours, or a start and end time.");
        }
        if (minutes <= 0)
        {
            throw new LedgerValidationException("A shift needs to be longer than zero.");
        }
        if (minutes > 24 * 60)
        {
            throw new LedgerValidationException("A single shift can't be longer than 24 hours.");
        }
        return minutes;
    }

    private static void Apply(Shift shift, ShiftInput input, BreakRule rule)
    {
        if (input.BreakOverrideMinutes < 0)
        {
            throw new LedgerValidationException("A break can't be negative.");
        }
        var scheduled = ScheduledMinutesFor(input);
        if (rule.PaidMinutesFor(scheduled, input.BreakOverrideMinutes) <= 0)
        {
            throw new LedgerValidationException("A shift needs more time than its unpaid break.");
        }
        shift.JobId = input.JobId;
        shift.Date = input.Date;
        shift.ScheduledMinutes = scheduled;
        shift.StartTime = input.StartTime;
        shift.EndTime = input.EndTime;
        shift.BreakOverrideMinutes = input.BreakOverrideMinutes;
        shift.Status = input.Status;
        var note = input.Note?.Trim();
        if (note?.Length > 200)
        {
            throw new LedgerValidationException("Shift notes can be up to 200 characters.");
        }
        shift.Note = string.IsNullOrEmpty(note) ? null : note;
    }

    private static void Apply(Job job, JobInput input)
    {
        job.PayFrequency = input.PayFrequency;
        job.PayPeriodAnchor = input.PayPeriodAnchor;
        job.PaydayOffsetDays = input.PaydayOffsetDays;
        job.IsPaydayConfirmed = input.IsPaydayConfirmed;
        job.WorkweekStart = input.WorkweekStart;
        job.IsOvertimeEligible = input.IsOvertimeEligible;
        job.DepositAccountId = input.DepositAccountId;
        job.IncomeCategoryId = input.IncomeCategoryId;
        job.FilingStatus = input.FilingStatus;
        job.W4MultipleJobs = input.W4MultipleJobs;
        job.W4DependentsCreditCents = input.W4DependentsCreditCents;
        job.W4OtherIncomeCents = input.W4OtherIncomeCents;
        job.W4DeductionsCents = input.W4DeductionsCents;
        job.W4ExtraWithholdingCents = input.W4ExtraWithholdingCents;
        job.WorkLocationName = string.IsNullOrWhiteSpace(input.WorkLocationName) ? null : input.WorkLocationName.Trim();
        job.WorkPsdCode = string.IsNullOrWhiteSpace(input.WorkPsdCode) ? null : input.WorkPsdCode.Trim();
        job.WorkNonResidentEitBasisPoints = input.WorkNonResidentEitBasisPoints;
        job.WorkLstAnnualCents = input.WorkLstAnnualCents;
        job.IsLstExempt = input.IsLstExempt;
        job.UnpaidBreakMinutes = input.UnpaidBreakMinutes;
        job.UnpaidBreakAfterMinutes = input.UnpaidBreakAfterMinutes;
        job.PaidBreakMinutes = input.PaidBreakMinutes;
    }

    private static void Validate(JobInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name))
            throw new LedgerValidationException("Give the job a name, like the employer.");
        if (input.Name.Trim().Length > 100)
            throw new LedgerValidationException("Job names can be up to 100 characters.");
        if (input.HourlyRateCents <= 0)
            throw new LedgerValidationException("Enter an hourly rate greater than zero.");
        if (input.HourlyRateCents > MaxHourlyRateCents)
            throw new LedgerValidationException("That hourly rate looks too high. Check for a typo.");
        if (!Enum.IsDefined(input.PayFrequency) || !Enum.IsDefined(input.FilingStatus) || !Enum.IsDefined(input.WorkweekStart))
            throw new LedgerValidationException("Choose a pay frequency, filing status and workweek start.");
        if (input.PaydayOffsetDays is < 0 or > 31)
            throw new LedgerValidationException("Payday should be between 0 and 31 days after the pay period ends.");
        if (input.WorkNonResidentEitBasisPoints is < 0 or > 1000)
            throw new LedgerValidationException("Enter a work-location tax rate between 0% and 10%.");
        if (input.WorkLstAnnualCents is < 0 or > 5200)
            throw new LedgerValidationException("The local services tax is at most $52 a year.");
        if (input.W4DependentsCreditCents < 0 || input.W4OtherIncomeCents < 0 || input.W4DeductionsCents < 0 || input.W4ExtraWithholdingCents < 0)
            throw new LedgerValidationException("W-4 amounts can't be negative.");
        if (input.UnpaidBreakMinutes is < 0 or > 240 || input.PaidBreakMinutes is < 0 or > 240)
            throw new LedgerValidationException("Breaks should be between 0 and 240 minutes.");
        if (input.UnpaidBreakAfterMinutes is < 0 or > 1440)
            throw new LedgerValidationException("The unpaid break should start applying at a shift length between 0 and 24 hours.");
    }

    private static async Task ValidateReferencesAsync(LedgerDbContext db, JobInput input, CancellationToken ct)
    {
        if (input.DepositAccountId is { } a && !await db.Accounts.AnyAsync(x => x.Id == a, ct))
            throw new LedgerValidationException("That account no longer exists.");
        if (input.IncomeCategoryId is { } c && !await db.Categories.AnyAsync(x => x.Id == c && x.CategoryType == CategoryType.Income, ct))
            throw new LedgerValidationException("Choose an income category.");
    }

    private static async Task<Job> RequireJobAsync(LedgerDbContext db, int jobId, CancellationToken ct) =>
        await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct)
        ?? throw new LedgerValidationException("That job no longer exists.");
}

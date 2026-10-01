using Ledger.Core.Forecasting;
using Ledger.Core.Data;
using Ledger.Core.Models;
using Ledger.Core.Payroll;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Core.Services;

/// <summary>The real paycheck the user recorded against a prediction.</summary>
public sealed record RecordedPaycheck(int TransactionId, long ActualCents);

/// <summary>
/// An estimated paycheck. This is a forecast, never money that exists; it becomes real only
/// when the user records the actual paycheck as a transaction.
/// </summary>
public sealed record PredictedPaycheck(
    int JobId,
    string JobName,
    PayPeriod Period,
    int RegularMinutes,
    int OvertimeMinutes,
    int ShiftCount,
    bool HasUnconfirmedShifts,
    bool IsPaydayConfirmed,
    long HourlyRateCents,
    PaycheckBreakdown Breakdown,
    IReadOnlyList<(DateOnly Date, long GrossCents)> EarningsByDate,
    RecordedPaycheck? Recorded,
    long OvertimePayCents = 0,
    PaycheckInput? Input = null,
    Paystub? Paystub = null)
{
    public long GrossCents => Breakdown.GrossCents;
    public long NetCents => Breakdown.NetCents;
    public int TotalMinutes => RegularMinutes + OvertimeMinutes;

    /// <summary>Gross pay for straight time: gross minus the overtime premium hours, so the two always add up exactly.</summary>
    public long RegularPayCents => GrossCents - OvertimePayCents;
}

/// <summary>Estimated income over a date range.</summary>
public sealed record IncomeWindow(string Title, DateOnly From, DateOnly To, long GrossCents, long NetCents, int PaycheckCount);

/// <summary>
/// Turns shifts into predicted paychecks (gross, withholding, net) and totals them over weeks
/// and months. Pure prediction: it never creates transactions.
/// </summary>
public sealed class IncomeForecastService(
    IDbContextFactory<LedgerDbContext> contextFactory,
    IncomeService income)
{
    /// <summary>How far before a range shifts are loaded, so year-to-date limits start from January 1.</summary>
    private const int LookBehindDays = 45;

    /// <summary>
    /// Predicted paychecks with a payday between <paramref name="from"/> and <paramref name="to"/>,
    /// in payday order. Year-to-date limits (Social Security cap, LST cap) are applied from
    /// January 1 of each payday's year.
    /// </summary>
    public Task<IReadOnlyList<PredictedPaycheck>> GetPaychecksAsync(DateOnly from, DateOnly to, CancellationToken ct = default) =>
        GetPaychecksAsync(from, to, null, ct);

    /// <summary>
    /// As above, with a what-if applied: hours scaled or replaced, or a new pay rate, from a date. The
    /// changed shifts are priced exactly like real ones (breaks, overtime, taxes). Nothing is saved.
    /// </summary>
    public async Task<IReadOnlyList<PredictedPaycheck>> GetPaychecksAsync(DateOnly from, DateOnly to, Scenario? scenario, CancellationToken ct = default)
    {
        var home = await income.GetHomeTaxSettingsAsync(ct);
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        var loadFrom = new DateOnly(from.Year, 1, 1).AddDays(-LookBehindDays);
        var jobs = await db.Jobs.AsNoTracking().Include(j => j.Rates).Where(j => !j.IsArchived).ToListAsync(ct);
        var shifts = await db.Shifts.AsNoTracking().Where(s => s.Date >= loadFrom && s.Date <= to).ToListAsync(ct);
        var records = await db.PaycheckRecords.AsNoTracking()
            .Where(r => r.PayDate >= from.AddDays(-LookBehindDays))
            .Select(r => new { r.JobId, r.PeriodStart, r.TransactionId, Amount = (long?)r.Transaction!.AmountCents })
            .ToListAsync(ct);
        var deductions = await db.JobDeductions.AsNoTracking().ToListAsync(ct);
        var stubs = await db.Paystubs.AsNoTracking().Where(s => s.PayDate >= from.AddDays(-LookBehindDays)).ToListAsync(ct);

        var result = new List<PredictedPaycheck>();
        foreach (var job in jobs)
        {
            var jobShifts = shifts.Where(s => s.JobId == job.Id).ToList();
            var rates = job.Rates.Select(r => new RateChange(r.EffectiveDate, r.HourlyRateCents)).ToList();
            if (scenario is not null)
            {
                (jobShifts, rates) = ApplyScenario(scenario, job, jobShifts, rates, to);
            }
            rates = rates.OrderBy(r => r.EffectiveDate).ToList();
            if (jobShifts.Count == 0 || rates.Count == 0)
            {
                continue;
            }

            var priced = GrossPayCalculator.Price(
                jobShifts.Select(s => new WorkedTime(s.Date, BreakRule.For(job).PaidMinutesFor(s.ScheduledMinutes, s.BreakOverrideMinutes), s.StartTime)),
                rates, job.WorkweekStart, job.IsOvertimeEligible);

            var periods = priced
                .GroupBy(p => PayPeriodCalendar.PeriodContaining(p.Date, job.PayFrequency, job.PayPeriodAnchor, job.PaydayOffsetDays))
                .OrderBy(g => g.Key.PayDate);

            long ytdWages = 0, ytdLst = 0;
            var ytdYear = 0;
            foreach (var period in periods)
            {
                var payYear = period.Key.PayDate.Year;
                if (payYear != ytdYear)
                {
                    (ytdYear, ytdWages, ytdLst) = (payYear, 0, 0);
                }

                var gross = GrossPayCalculator.Gross(period);
                var input = new PaycheckInput
                {
                    GrossCents = gross,
                    Deductions = DeductionsFor(deductions, job.Id, period.Key.End, gross),
                    Frequency = job.PayFrequency,
                    FilingStatus = job.FilingStatus,
                    W4MultipleJobs = job.W4MultipleJobs,
                    W4DependentsCreditCents = job.W4DependentsCreditCents,
                    W4OtherIncomeCents = job.W4OtherIncomeCents,
                    W4DeductionsCents = job.W4DeductionsCents,
                    W4ExtraWithholdingCents = job.W4ExtraWithholdingCents,
                    YearToDateFicaWagesCents = ytdWages,
                    ResidentEitBasisPoints = home.ResidentEitBasisPoints,
                    WorkNonResidentEitBasisPoints = job.WorkNonResidentEitBasisPoints,
                    WorkLstAnnualCents = job.WorkLstAnnualCents,
                    YearToDateLstCents = ytdLst,
                    IsLstExempt = job.IsLstExempt,
                    PennsylvaniaTaxes = home.InPennsylvania,
                };
                var breakdown = PayrollTaxCalculator.Calculate(input, payYear);

                ytdWages += gross - breakdown.Amount(PaycheckLineKind.PreTaxHealth); // Section 125 isn't FICA wages
                ytdLst += breakdown.Amount(PaycheckLineKind.LocalServicesTax);

                if (period.Key.PayDate < from || period.Key.PayDate > to)
                {
                    continue;
                }

                var periodShifts = jobShifts.Where(s => s.Date >= period.Key.Start && s.Date <= period.Key.End).ToList();
                var record = records.FirstOrDefault(r => r.JobId == job.Id && r.PeriodStart == period.Key.Start);
                result.Add(new PredictedPaycheck(
                    job.Id,
                    job.Name,
                    period.Key,
                    period.Sum(p => p.RegularMinutes),
                    period.Sum(p => p.OvertimeMinutes),
                    periodShifts.Count,
                    periodShifts.Any(s => s.Status == ShiftStatus.Scheduled),
                    job.IsPaydayConfirmed,
                    GrossPayCalculator.RateOn(period.Key.End, rates),
                    breakdown,
                    period.GroupBy(p => p.Date)
                        .Select(d => (d.Key, Money.RoundToCents(d.Sum(p => p.Earnings(GrossPayCalculator.OvertimeMultiplier)))))
                        .ToList(),
                    record?.Amount is { } amount ? new RecordedPaycheck(record.TransactionId, amount) : null,
                    // Each overtime minute at its own day's rate (a raise mid-period is priced correctly).
                    Money.RoundToCents(period.Sum(p => p.OvertimeMinutes * (decimal)p.HourlyRateCents * GrossPayCalculator.OvertimeMultiplier / 60m)),
                    input,
                    stubs.FirstOrDefault(s => s.JobId == job.Id && s.PeriodStart == period.Key.Start)));
            }
        }
        return result.OrderBy(p => p.Period.PayDate).ThenBy(p => p.JobName).ToList();
    }

    /// <summary>
    /// The job's deductions in effect for a pay period ending on <paramref name="periodEnd"/>, in
    /// cents for this paycheck. A percentage is applied to this paycheck's gross, rounded to the cent.
    /// </summary>
    internal static IReadOnlyList<PaycheckDeduction> DeductionsFor(IEnumerable<JobDeduction> all, int jobId, DateOnly periodEnd, long gross) =>
        all.Where(d => d.JobId == jobId && d.StartDate <= periodEnd && (d.EndDate is null || d.EndDate >= periodEnd))
           .OrderBy(d => d.Kind).ThenBy(d => d.Id)
           .Select(d => new PaycheckDeduction(d.Name, d.Kind,
               d.PercentBasisPoints is { } bps ? Money.RoundToCents(gross * bps / 10_000m) : d.AmountCents))
           .ToList();

    /// <summary>
    /// Estimated income for this week (Sunday–Saturday), the next two weeks, and this month.
    /// By payday counts paychecks paid in the window (when money arrives). By work date counts
    /// what was earned in the window, whenever it is paid; its net is the paycheck's net share.
    /// </summary>
    public async Task<IReadOnlyList<IncomeWindow>> GetWindowsAsync(DateOnly today, bool byWorkDate, CancellationToken ct = default)
    {
        var weekStart = today.AddDays(-(int)today.DayOfWeek);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        (string Title, DateOnly From, DateOnly To)[] windows =
        [
            ("This week", weekStart, weekStart.AddDays(6)),
            ("Next 2 weeks", today, today.AddDays(13)),
            ("This month", monthStart, monthStart.AddMonths(1).AddDays(-1)),
        ];

        var from = windows.Min(w => w.From);
        var to = windows.Max(w => w.To);
        // Earnings in a window can be paid up to a pay period plus the payday offset later.
        var paychecks = await GetPaychecksAsync(from, byWorkDate ? to.AddDays(LookBehindDays + 31) : to, ct);

        return windows.Select(w =>
        {
            if (!byWorkDate)
            {
                var inWindow = paychecks.Where(p => p.Period.PayDate >= w.From && p.Period.PayDate <= w.To).ToList();
                return new IncomeWindow(w.Title, w.From, w.To, inWindow.Sum(p => p.GrossCents), inWindow.Sum(p => p.NetCents), inWindow.Count);
            }

            long gross = 0;
            decimal net = 0;
            var count = 0;
            foreach (var p in paychecks)
            {
                var earned = p.EarningsByDate.Where(e => e.Date >= w.From && e.Date <= w.To).Sum(e => e.GrossCents);
                if (earned == 0) continue;
                gross += earned;
                net += p.GrossCents == 0 ? 0 : (decimal)earned * p.NetCents / p.GrossCents;
                count++;
            }
            return new IncomeWindow(w.Title, w.From, w.To, gross, Money.RoundToCents(net), count);
        }).ToList();
    }

    /// <summary>What-if hours and pay for one job: in-memory copies only.</summary>
    private static (List<Shift> Shifts, List<RateChange> Rates) ApplyScenario(Scenario scenario, Job job, List<Shift> shifts, List<RateChange> rates, DateOnly to)
    {
        foreach (var change in scenario.Changes.Where(c => c.JobId is null || c.JobId == job.Id))
        {
            switch (change.Kind)
            {
                case ScenarioChangeKind.PayRate when change.AmountCents > 0:
                    rates = rates.Where(r => r.EffectiveDate != change.From).Append(new RateChange(change.From, change.AmountCents)).ToList();
                    break;
                case ScenarioChangeKind.HoursPercent:
                    shifts = shifts.Select(s => s.Date < change.From ? s : new Shift
                    {
                        JobId = s.JobId, Date = s.Date, StartTime = s.StartTime, BreakOverrideMinutes = s.BreakOverrideMinutes, Status = s.Status,
                        ScheduledMinutes = (int)Math.Round(s.ScheduledMinutes * Math.Max(0, change.Percent) / 100.0),
                    }).Where(s => s.ScheduledMinutes > 0).ToList();
                    break;
                case ScenarioChangeKind.WeeklyHours:
                    // Every workweek from the date: the paid minutes over four equal shifts with no unpaid
                    // break, so the hours entered are exactly the hours paid. Overtime still applies past 40.
                    var kept = shifts.Where(s => s.Date < change.From).ToList();
                    var weekStart = change.From.AddDays(-(((int)change.From.DayOfWeek - (int)job.WorkweekStart + 7) % 7));
                    for (var week = weekStart; week <= to; week = week.AddDays(7))
                    {
                        var perShift = change.WeeklyMinutes / 4;
                        for (var day = 0; day < 4 && change.WeeklyMinutes > 0; day++)
                        {
                            var date = week.AddDays(day);
                            var minutes = perShift + (day == 0 ? change.WeeklyMinutes % 4 : 0);
                            if (date < change.From || minutes <= 0) continue;
                            kept.Add(new Shift { JobId = job.Id, Date = date, ScheduledMinutes = Math.Min(minutes, 1440), BreakOverrideMinutes = 0, Status = ShiftStatus.Scheduled });
                        }
                    }
                    shifts = kept;
                    break;
            }
        }
        return (shifts, rates);
    }

    /// <summary>
    /// Links a recorded income transaction to the paycheck it pays. The estimate is kept as it was,
    /// so the two can be compared. Recording again for the same period replaces the link.
    /// </summary>
    public async Task RecordPaycheckAsync(int jobId, PayPeriod period, long estimatedNetCents, int transactionId, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var transaction = await db.Transactions.AsNoTracking().FirstOrDefaultAsync(t => t.Id == transactionId, ct)
            ?? throw new LedgerValidationException("That transaction no longer exists.");
        if (transaction.TransactionType != TransactionType.Income)
        {
            throw new LedgerValidationException("A paycheck must be recorded as income.");
        }

        // Include a record whose transaction was deleted: it is reused, never duplicated (unique index).
        var record = await db.PaycheckRecords.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.JobId == jobId && r.PeriodStart == period.Start, ct);
        if (record is null)
        {
            db.PaycheckRecords.Add(record = new PaycheckRecord { JobId = jobId, PeriodStart = period.Start });
        }
        record.PayDate = period.PayDate;
        record.EstimatedNetCents = estimatedNetCents;
        record.TransactionId = transactionId;
        await db.SaveChangesAsync(ct);
    }
}

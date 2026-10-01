using Ledger.Core.Models;
using Ledger.Core.Payroll;
using Ledger.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Core.Tests;

public class PaycheckDeductionTests
{
    private static PaycheckInput Weekly(long gross, params PaycheckDeduction[] deductions) => new()
    {
        GrossCents = gross,
        Frequency = PayFrequency.Weekly,
        ResidentEitBasisPoints = 150,
        WorkNonResidentEitBasisPoints = 50,
        Deductions = deductions,
    };

    [Fact]
    public void Pre_tax_retirement_lowers_only_federal_tax()
    {
        var plain = PayrollTaxCalculator.Calculate(Weekly(90000), 2026);
        var with401k = PayrollTaxCalculator.Calculate(Weekly(90000, new PaycheckDeduction("401(k)", DeductionKind.PreTaxRetirement, 4500)), 2026);

        Assert.True(with401k.Amount(PaycheckLineKind.FederalIncomeTax) < plain.Amount(PaycheckLineKind.FederalIncomeTax));
        foreach (var kind in new[] { PaycheckLineKind.SocialSecurity, PaycheckLineKind.Medicare, PaycheckLineKind.PennsylvaniaIncomeTax,
                     PaycheckLineKind.LocalEarnedIncomeTax, PaycheckLineKind.PennsylvaniaUnemployment })
        {
            Assert.Equal(plain.Amount(kind), with401k.Amount(kind)); // PA and local tax 401(k) deferrals
        }
        var line = Assert.Single(with401k.Lines, l => l.Kind == PaycheckLineKind.PreTaxRetirement);
        Assert.Equal(("401(k)", 4500L), (line.Label, line.AmountCents));
    }

    [Fact]
    public void Section_125_health_lowers_federal_fica_state_and_local_but_not_unemployment()
    {
        var plain = PayrollTaxCalculator.Calculate(Weekly(90000), 2026);
        var withDental = PayrollTaxCalculator.Calculate(Weekly(90000, new PaycheckDeduction("Dental", DeductionKind.PreTaxHealth, 2000)), 2026);

        Assert.Equal(Money.RoundToCents(88000 * 0.062m), withDental.Amount(PaycheckLineKind.SocialSecurity));
        Assert.Equal(Money.RoundToCents(88000 * 0.0145m), withDental.Amount(PaycheckLineKind.Medicare));
        Assert.Equal(Money.RoundToCents(88000 * 0.0307m), withDental.Amount(PaycheckLineKind.PennsylvaniaIncomeTax));
        Assert.Equal(Money.RoundToCents(88000 * 0.015m), withDental.Amount(PaycheckLineKind.LocalEarnedIncomeTax));
        Assert.Equal(plain.Amount(PaycheckLineKind.PennsylvaniaUnemployment), withDental.Amount(PaycheckLineKind.PennsylvaniaUnemployment)); // on gross
        Assert.True(withDental.Amount(PaycheckLineKind.FederalIncomeTax) < plain.Amount(PaycheckLineKind.FederalIncomeTax));
    }

    [Fact]
    public void After_tax_deductions_change_take_home_only()
    {
        var plain = PayrollTaxCalculator.Calculate(Weekly(90000), 2026);
        var withDues = PayrollTaxCalculator.Calculate(Weekly(90000, new PaycheckDeduction("Union dues", DeductionKind.AfterTax, 1500)), 2026);

        Assert.Equal(plain.NetCents - 1500, withDues.NetCents);
        Assert.Equal(plain.Lines.Where(l => l.Kind != PaycheckLineKind.AfterTax).Sum(l => l.AmountCents),
            withDues.Lines.Where(l => l.Kind != PaycheckLineKind.AfterTax).Sum(l => l.AmountCents));
    }

    [Fact]
    public void Deductions_never_exceed_pay_or_push_take_home_below_zero()
    {
        var huge = PayrollTaxCalculator.Calculate(Weekly(5000,
            new PaycheckDeduction("Health", DeductionKind.PreTaxHealth, 4000),
            new PaycheckDeduction("401(k)", DeductionKind.PreTaxRetirement, 4000),
            new PaycheckDeduction("Dues", DeductionKind.AfterTax, 9999)), 2026);

        Assert.Equal(4000, huge.Amount(PaycheckLineKind.PreTaxHealth));
        Assert.True(huge.Amount(PaycheckLineKind.PreTaxRetirement) < 1000); // trimmed so the taxes on it fit
        Assert.Equal(0, huge.NetCents);
        Assert.All(huge.Lines, l => Assert.True(l.AmountCents >= 0));
    }
}

public class DeductionAndPaystubServiceTests
{
    private static readonly DateOnly Today = new(2026, 10, 15);
    private static DateOnly D(int m, int d) => new(2026, m, d);

    private sealed class Fixture : IAsyncDisposable
    {
        public required TestDatabase Db { get; init; }
        public required IncomeService Income { get; init; }
        public required IncomeForecastService Paychecks { get; init; }
        public required DeductionService Deductions { get; init; }
        public required PaystubService Paystubs { get; init; }
        public required Job Job { get; init; }
        public ValueTask DisposeAsync() => Db.DisposeAsync();

        public Task AddShiftAsync(int month, int day, int minutes) =>
            Income.AddShiftAsync(new ShiftInput(Job.Id, D(month, day), minutes, null, null, 0, ShiftStatus.Worked, null));

        public async Task<PredictedPaycheck> PaycheckOnAsync(DateOnly payday) =>
            Assert.Single(await Paychecks.GetPaychecksAsync(payday, payday));
    }

    private static async Task<Fixture> CreateAsync()
    {
        var db = await TestDatabase.CreateAsync(Today);
        var income = new IncomeService(db.Factory, new SettingsService(db.Factory));
        await income.SetHomeTaxSettingsAsync(IncomeForecastTests.MapleBoro);
        var job = await income.CreateJobAsync(new JobInput("Riverside Market", 1350, D(9, 1), PayFrequency.Weekly, D(9, 20), 6, false, DayOfWeek.Sunday, true,
            null, null, FilingStatus.Single, false, 0, 0, 0, 0, "Riverton City", "990201", 50, 5200, false));
        return new Fixture
        {
            Db = db,
            Income = income,
            Paychecks = new IncomeForecastService(db.Factory, income),
            Deductions = new DeductionService(db.Factory),
            Paystubs = new PaystubService(db.Factory),
            Job = job,
        };
    }

    // ---- Deductions ---------------------------------------------------------------------------

    [Fact]
    public async Task A_percent_deduction_is_taken_from_each_paychecks_gross()
    {
        await using var f = await CreateAsync();
        await f.Deductions.SaveAsync(f.Job.Id, [new DeductionInput(null, "401(k)", DeductionKind.PreTaxRetirement, 0, 500, D(9, 1))], Today);
        await f.AddShiftAsync(9, 29, 481); // 8 h 1 min at $13.50 = $108.23

        var p = await f.PaycheckOnAsync(D(10, 9));
        Assert.Equal(10823, p.GrossCents);
        Assert.Equal(541, p.Breakdown.Amount(PaycheckLineKind.PreTaxRetirement)); // 5% = $5.4115 → $5.41
    }

    [Fact]
    public async Task Changing_a_deduction_that_already_applied_starts_a_new_version_and_old_estimates_stay()
    {
        await using var f = await CreateAsync();
        await f.Deductions.SaveAsync(f.Job.Id, [new DeductionInput(null, "Dental", DeductionKind.PreTaxHealth, 500, null, D(9, 1))], Today);
        await f.AddShiftAsync(9, 29, 480);   // paid Oct 9
        await f.AddShiftAsync(10, 20, 480);  // paid Oct 30
        var before = await f.PaycheckOnAsync(D(10, 9));

        var dental = Assert.Single(await f.Deductions.GetCurrentAsync(f.Job.Id, Today));
        await f.Deductions.SaveAsync(f.Job.Id, [new DeductionInput(dental.Id, "Dental plan", DeductionKind.PreTaxHealth, 800, null, dental.StartDate)], Today);

        await using (var db = f.Db.Context())
        {
            var all = await db.JobDeductions.OrderBy(d => d.Id).ToListAsync();
            Assert.Equal([(500L, D(9, 1), (DateOnly?)D(10, 14)), (800L, Today, null)], all.Select(d => (d.AmountCents, d.StartDate, d.EndDate)));
            Assert.Equal(["Dental", "Dental plan"], all.Select(d => d.Name)); // the old version keeps the name it had
        }
        Assert.Equal(before.NetCents, (await f.PaycheckOnAsync(D(10, 9))).NetCents); // the past didn't move
        Assert.Equal(800, (await f.PaycheckOnAsync(D(10, 30))).Breakdown.Amount(PaycheckLineKind.PreTaxHealth));
    }

    [Fact]
    public async Task Removing_ends_a_used_deduction_but_deletes_one_that_never_applied()
    {
        await using var f = await CreateAsync();
        await f.Deductions.SaveAsync(f.Job.Id,
        [
            new DeductionInput(null, "Old dues", DeductionKind.AfterTax, 500, null, D(9, 1)),
            new DeductionInput(null, "Future 401(k)", DeductionKind.PreTaxRetirement, 0, 300, D(11, 1)),
        ], Today);
        await f.Deductions.SaveAsync(f.Job.Id, [], Today);

        await using var db = f.Db.Context();
        var left = Assert.Single(await db.JobDeductions.ToListAsync());
        Assert.Equal(("Old dues", (DateOnly?)D(10, 14)), (left.Name, left.EndDate));
        Assert.Empty(await f.Deductions.GetCurrentAsync(f.Job.Id, Today));
    }

    [Fact]
    public async Task Invalid_deductions_are_rejected()
    {
        await using var f = await CreateAsync();
        DeductionInput ok = new(null, "Dues", DeductionKind.AfterTax, 500, null, Today);
        foreach (var bad in new[] { ok with { Name = " " }, ok with { AmountCents = 0 }, ok with { PercentBasisPoints = 0 },
                     ok with { PercentBasisPoints = 10_001 }, ok with { Kind = (DeductionKind)9 } })
        {
            await Assert.ThrowsAsync<LedgerValidationException>(() => f.Deductions.SaveAsync(f.Job.Id, [bad], Today));
        }
    }

    // ---- Paystubs -----------------------------------------------------------------------------

    /// <summary>A paystub exactly as Ledger would predict it.</summary>
    private static PaystubInput StubFrom(PredictedPaycheck p) => new(
        p.Period.PayDate, p.GrossCents, p.NetCents, p.TotalMinutes, p.Period.Start, p.Period.End,
        p.Breakdown.Amount(PaycheckLineKind.FederalIncomeTax), p.Breakdown.Amount(PaycheckLineKind.SocialSecurity),
        p.Breakdown.Amount(PaycheckLineKind.Medicare), p.Breakdown.Amount(PaycheckLineKind.PennsylvaniaIncomeTax),
        p.Breakdown.Amount(PaycheckLineKind.PennsylvaniaUnemployment), p.Breakdown.Amount(PaycheckLineKind.LocalEarnedIncomeTax),
        p.Breakdown.Amount(PaycheckLineKind.LocalServicesTax));

    private static async Task<(PredictedPaycheck Estimate, PaystubReport Report)> CompareAsync(Fixture f, PredictedPaycheck p, PaystubInput stub)
    {
        await f.Paystubs.SaveAsync(f.Job.Id, p.Period.Start, stub);
        var withStub = await f.PaycheckOnAsync(p.Period.PayDate);
        return (withStub, PaystubComparison.Compare(withStub, withStub.Paystub!));
    }

    [Fact]
    public async Task A_paystub_that_matches_says_so()
    {
        await using var f = await CreateAsync();
        await f.AddShiftAsync(9, 29, 480);
        var p = await f.PaycheckOnAsync(D(10, 9));

        var (_, report) = await CompareAsync(f, p, StubFrom(p));

        Assert.True(report.AllMatch);
        Assert.StartsWith("Your paystub matches", report.Hints[0]);
        Assert.All(report.Lines, l => Assert.Equal(StubLineStatus.Matches, l.Status));
    }

    [Fact]
    public async Task Different_hours_are_blamed_on_hours_and_taxes_still_match_on_the_stubs_own_gross()
    {
        await using var f = await CreateAsync();
        await f.AddShiftAsync(9, 29, 480);
        var p = await f.PaycheckOnAsync(D(10, 9));

        // Really worked 9 hours: build the stub from Ledger's own math at that gross.
        var actual = PayrollTaxCalculator.Calculate(p.Input! with { GrossCents = 9 * 1350 }, 2026);
        var stub = StubFrom(p) with
        {
            GrossCents = actual.GrossCents, NetCents = actual.NetCents, PaidMinutes = 540,
            FederalCents = actual.Amount(PaycheckLineKind.FederalIncomeTax), SocialSecurityCents = actual.Amount(PaycheckLineKind.SocialSecurity),
            MedicareCents = actual.Amount(PaycheckLineKind.Medicare), StateCents = actual.Amount(PaycheckLineKind.PennsylvaniaIncomeTax),
            StateUnemploymentCents = actual.Amount(PaycheckLineKind.PennsylvaniaUnemployment), LocalEitCents = actual.Amount(PaycheckLineKind.LocalEarnedIncomeTax),
        };
        var (_, report) = await CompareAsync(f, p, stub);

        var hint = Assert.Single(report.Hints);
        Assert.Contains("9 h paid; Ledger has 8 h", hint);
        Assert.All(report.Lines.Where(l => l.Label is not ("Gross pay" or "Paid hours (minutes)")), l => Assert.Equal(StubLineStatus.Matches, l.Status));
    }

    [Fact]
    public async Task Each_tax_difference_points_at_its_likely_cause()
    {
        await using var f = await CreateAsync();
        await f.AddShiftAsync(9, 29, 480);
        var p = await f.PaycheckOnAsync(D(10, 9));
        var exact = StubFrom(p);

        // Federal off: W-4 hint.
        var (_, fed) = await CompareAsync(f, p, exact with { FederalCents = exact.FederalCents + 500, NetCents = exact.NetCents - 500 });
        Assert.Contains(fed.Hints, h => h.Contains("W-4"));

        // Social Security taken on $20 less: a pre-tax benefit.
        var lowerSs = Money.RoundToCents((p.GrossCents - 2000) * 0.062m);
        var (_, ss) = await CompareAsync(f, p, exact with { SocialSecurityCents = lowerSs });
        Assert.Contains(ss.Hints, h => h.Contains("pre-tax benefit"));

        // Local tax at 1%: the implied rate is named.
        var (_, local) = await CompareAsync(f, p, exact with { LocalEitCents = Money.RoundToCents(p.GrossCents * 0.01m) });
        Assert.Contains(local.Hints, h => h.Contains("is 1% of wages") && h.Contains("uses 1.5%"));

        // $7 more taken than Ledger knows about: an unknown deduction.
        var (_, other) = await CompareAsync(f, p, exact with { NetCents = exact.NetCents - 700 });
        Assert.Contains(other.Hints, h => h.Contains("$7.00 was taken for something Ledger doesn't know about"));
    }

    [Fact]
    public async Task Confirming_the_payday_from_a_paystub_moves_future_estimates()
    {
        await using var f = await CreateAsync();
        await f.AddShiftAsync(9, 29, 480);
        await f.AddShiftAsync(10, 6, 480);
        var p = await f.PaycheckOnAsync(D(10, 9)); // assumed: Friday after the Sun–Sat week

        // The real stub: period Sep 27 – Oct 3, paid Thursday Oct 8.
        var (_, report) = await CompareAsync(f, p, StubFrom(p) with { PayDate = D(10, 8) });
        var payday = Assert.IsType<PaydaySuggestion>(report.Payday);
        Assert.Equal((D(9, 27), 5, DayOfWeek.Thursday), (payday.PeriodStart, payday.OffsetDays, payday.PayWeekday));

        await f.Paystubs.ConfirmPaydayAsync(f.Job.Id, payday);
        var paydays = (await f.Paychecks.GetPaychecksAsync(D(10, 1), D(10, 31))).Select(x => x.Period.PayDate);
        Assert.Equal([D(10, 8), D(10, 15)], paydays);
        Assert.True((await f.Income.GetJobAsync(f.Job.Id)).IsPaydayConfirmed);
    }

    [Fact]
    public async Task Paystubs_are_validated_and_saving_again_replaces()
    {
        await using var f = await CreateAsync();
        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Paystubs.SaveAsync(f.Job.Id, D(9, 27), new PaystubInput(D(10, 9), 100, 200)));
        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Paystubs.SaveAsync(f.Job.Id, D(9, 27), new PaystubInput(D(10, 9), -1, 0)));

        await f.Paystubs.SaveAsync(f.Job.Id, D(9, 27), new PaystubInput(D(10, 9), 10800, 9400));
        await f.Paystubs.SaveAsync(f.Job.Id, D(9, 27), new PaystubInput(D(10, 9), 10800, 9500));
        await using var db = f.Db.Context();
        Assert.Equal(9500, Assert.Single(await db.Paystubs.ToListAsync()).NetCents);
    }
}

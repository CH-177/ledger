using Ledger.Core.Models;

namespace Ledger.Core.Payroll;

/// <summary>Everything that determines one paycheck's withholding.</summary>
public sealed record PaycheckInput
{
    public required long GrossCents { get; init; }
    public required PayFrequency Frequency { get; init; }

    /// <summary>Pre-tax 401(k)/403(b) this period: reduces federal income tax only.</summary>
    public long PreTaxRetirementCents { get; init; }

    /// <summary>Pre-tax Section 125 health/dental/vision this period: reduces federal, FICA and PA tax.</summary>
    public long PreTaxHealthCents { get; init; }

    /// <summary>
    /// Named deductions this period, each shown as its own line. Added to the two totals above for the
    /// tax bases. After-tax ones are taken last and never push take-home pay below zero.
    /// </summary>
    public IReadOnlyList<PaycheckDeduction> Deductions { get; init; } = [];

    // Form W-4 (2020 or later)
    public FilingStatus FilingStatus { get; init; } = FilingStatus.Single;
    public bool W4MultipleJobs { get; init; }
    public long W4DependentsCreditCents { get; init; }
    public long W4OtherIncomeCents { get; init; }
    public long W4DeductionsCents { get; init; }
    public long W4ExtraWithholdingCents { get; init; }

    /// <summary>Wages already paid this calendar year by this employer that were subject to FICA.</summary>
    public long YearToDateFicaWagesCents { get; init; }

    /// <summary>Home municipality + school district resident EIT, basis points (MapleBoro: 150).</summary>
    public int ResidentEitBasisPoints { get; init; }

    /// <summary>Work municipality's non-resident EIT, basis points (Riverton City: 50).</summary>
    public int WorkNonResidentEitBasisPoints { get; init; }

    /// <summary>Work location's Local Services Tax for the year, cents.</summary>
    public long WorkLstAnnualCents { get; init; }

    /// <summary>LST already withheld this calendar year.</summary>
    public long YearToDateLstCents { get; init; }

    public bool IsLstExempt { get; init; }

    /// <summary>
    /// False for someone living and working outside Pennsylvania: only federal tax and FICA are
    /// estimated (no PA income tax, PA unemployment or local taxes).
    /// </summary>
    public bool PennsylvaniaTaxes { get; init; } = true;
}

public enum PaycheckLineKind
{
    FederalIncomeTax,
    SocialSecurity,
    Medicare,
    PennsylvaniaIncomeTax,
    PennsylvaniaUnemployment,
    LocalEarnedIncomeTax,
    LocalServicesTax,
    PreTaxRetirement,
    PreTaxHealth,
    AfterTax,
}

/// <summary>A deduction amount for one paycheck (a percentage has already been applied to gross).</summary>
public sealed record PaycheckDeduction(string Name, DeductionKind Kind, long AmountCents);

/// <summary>One deduction on a paycheck.</summary>
public sealed record PaycheckLine(PaycheckLineKind Kind, string Label, long AmountCents, string Explanation);

/// <summary>An itemised paycheck: gross, each deduction, and net.</summary>
public sealed record PaycheckBreakdown(long GrossCents, IReadOnlyList<PaycheckLine> Lines, int TaxYear, bool TaxTablesExact)
{
    public long DeductionsCents => Lines.Sum(l => l.AmountCents);
    public long NetCents => GrossCents - DeductionsCents;
    public long Amount(PaycheckLineKind kind) => Lines.Where(l => l.Kind == kind).Sum(l => l.AmountCents);
}

/// <summary>
/// Estimates withholding for one paycheck of a Pennsylvania W-2 employee. The result is what
/// the employer withholds, not the year's final tax liability.
/// </summary>
public static class PayrollTaxCalculator
{
    public static PaycheckBreakdown Calculate(PaycheckInput input, int taxYear)
    {
        if (input.GrossCents < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "Gross pay can't be negative.");
        }
        var tables = TaxTables.For(taxYear, out var exact);
        var periods = PayPeriodCalendar.PeriodsPerYear(input.Frequency);
        var gross = input.GrossCents;
        var lines = new List<PaycheckLine>();

        // Pre-tax deductions, health first, each capped so they never exceed gross pay.
        var room = gross;
        long TakePreTax(PaycheckLineKind kind, string label, long amount, string explanation)
        {
            var taken = Math.Clamp(amount, 0, room);
            room -= taken;
            if (taken > 0)
            {
                lines.Add(new(kind, label, taken, explanation));
            }
            return taken;
        }
        const string HealthNote = "Section 125: lowers federal, Social Security, Medicare, PA and local tax.";
        const string RetirementNote = "Lowers federal income tax. Pennsylvania and local tax still apply.";
        var health = TakePreTax(PaycheckLineKind.PreTaxHealth, "Health benefits (pre-tax)", input.PreTaxHealthCents, HealthNote)
                     + input.Deductions.Where(d => d.Kind == DeductionKind.PreTaxHealth)
                         .Sum(d => TakePreTax(PaycheckLineKind.PreTaxHealth, d.Name, d.AmountCents, HealthNote));
        var retirement = TakePreTax(PaycheckLineKind.PreTaxRetirement, "Retirement (pre-tax)", input.PreTaxRetirementCents, RetirementNote)
                         + input.Deductions.Where(d => d.Kind == DeductionKind.PreTaxRetirement)
                             .Sum(d => TakePreTax(PaycheckLineKind.PreTaxRetirement, d.Name, d.AmountCents, RetirementNote));

        // Federal income tax: Pub 15-T Worksheet 1A (annual percentage method, W-4 2020+).
        var federalWages = gross - health - retirement;
        lines.Add(new(PaycheckLineKind.FederalIncomeTax, "Federal income tax", FederalWithholding(input, federalWages, periods, tables),
            input.W4MultipleJobs ? "IRS 2026 tables, W-4 multiple jobs box checked." : "IRS 2026 tables, based on your W-4."));

        // FICA: Section 125 is exempt; 401(k) deferrals are not.
        var ficaWages = gross - health;
        var ssRoom = Math.Max(0, tables.SocialSecurity.WageBaseCents - input.YearToDateFicaWagesCents);
        lines.Add(new(PaycheckLineKind.SocialSecurity, "Social Security",
            Round(Math.Min(ficaWages, ssRoom) * tables.SocialSecurity.Rate),
            $"{Percent(tables.SocialSecurity.Rate)} up to {Money.FormatCompact(tables.SocialSecurity.WageBaseCents)} a year."));

        var overThreshold = Math.Max(0, input.YearToDateFicaWagesCents + ficaWages - tables.Medicare.AdditionalThresholdCents)
                            - Math.Max(0, input.YearToDateFicaWagesCents - tables.Medicare.AdditionalThresholdCents);
        lines.Add(new(PaycheckLineKind.Medicare, "Medicare",
            Round(ficaWages * tables.Medicare.Rate + overThreshold * tables.Medicare.AdditionalRate),
            $"{Percent(tables.Medicare.Rate)} (plus {Percent(tables.Medicare.AdditionalRate)} over {Money.FormatCompact(tables.Medicare.AdditionalThresholdCents)})."));

        if (input.PennsylvaniaTaxes)
        {
            // Pennsylvania taxes 401(k) deferrals; only Section 125 is excluded.
            var paWages = gross - health;
            lines.Add(new(PaycheckLineKind.PennsylvaniaIncomeTax, "PA income tax", Round(paWages * tables.Pennsylvania.IncomeTaxRate),
                $"{Percent(tables.Pennsylvania.IncomeTaxRate)} flat."));
            lines.Add(new(PaycheckLineKind.PennsylvaniaUnemployment, "PA unemployment", Round(gross * tables.Pennsylvania.UnemploymentEmployeeRate),
                $"{Percent(tables.Pennsylvania.UnemploymentEmployeeRate)} employee share."));

            // Act 32: withhold the higher of the home resident rate and the work non-resident rate.
            var eitBps = Math.Max(input.ResidentEitBasisPoints, input.WorkNonResidentEitBasisPoints);
            var eitSource = input.ResidentEitBasisPoints >= input.WorkNonResidentEitBasisPoints ? "your home rate" : "your work location's rate";
            lines.Add(new(PaycheckLineKind.LocalEarnedIncomeTax, "Local earned income tax", Round(paWages * eitBps / 10_000m),
                $"{eitBps / 100m:0.##}%, {eitSource} (the higher of home and work applies)."));

            var lst = LocalServicesTax(input, periods, tables);
            if (lst > 0)
            {
                lines.Add(new(PaycheckLineKind.LocalServicesTax, "Local services tax", lst,
                    $"{Money.FormatCompact(input.WorkLstAnnualCents)} a year, set by where you work."));
            }
        }

        // PA, local and FICA still tax 401(k) money, so large pre-tax deductions on a tiny paycheck
        // could leave nothing for the taxes. An employer can't take more than you earned: shrink
        // retirement, then health, by any shortfall.
        var shortfall = lines.Sum(l => l.AmountCents) - gross;
        foreach (var kind in new[] { PaycheckLineKind.PreTaxRetirement, PaycheckLineKind.PreTaxHealth })
        {
            for (var i = lines.Count - 1; i >= 0 && shortfall > 0; i--)
            {
                if (lines[i].Kind != kind) continue;
                var cut = Math.Min(shortfall, lines[i].AmountCents);
                lines[i] = lines[i] with { AmountCents = lines[i].AmountCents - cut };
                shortfall -= cut;
            }
        }
        lines.RemoveAll(l => l.AmountCents == 0 && l.Kind is PaycheckLineKind.PreTaxRetirement or PaycheckLineKind.PreTaxHealth);

        // After-tax deductions come out of what's left, never below zero take-home.
        var left = gross - lines.Sum(l => l.AmountCents);
        foreach (var d in input.Deductions.Where(d => d.Kind == DeductionKind.AfterTax))
        {
            var taken = Math.Clamp(d.AmountCents, 0, Math.Max(0, left));
            left -= taken;
            if (taken > 0)
            {
                lines.Add(new(PaycheckLineKind.AfterTax, d.Name, taken, "Taken after taxes; doesn't change them."));
            }
        }

        return new PaycheckBreakdown(gross, lines, tables.Year, exact);
    }

    /// <summary>Pub 15-T Worksheet 1A, Forms W-4 from 2020 or later.</summary>
    internal static long FederalWithholding(PaycheckInput input, long periodWagesCents, int periods, TaxTables tables)
    {
        // Step 1: annualise and adjust.
        var annual = (decimal)periodWagesCents * periods + input.W4OtherIncomeCents;                // 1c + 1d = 1e
        var subtract = input.W4DeductionsCents                                                      // 1f
                       + (input.W4MultipleJobs ? 0 : tables.Federal.Step1gCents[input.FilingStatus]); // 1g
        var adjusted = Math.Max(0, annual - subtract);                                              // 1i

        // Step 2: tentative withholding from the annual table.
        var schedule = (input.W4MultipleJobs ? tables.Federal.Step2 : tables.Federal.Standard)[input.FilingStatus];
        var row = schedule.Last(r => adjusted >= r.Over);
        var annualTax = row.Base + (adjusted - row.Over) * row.Rate;                                // 2g
        var perPeriod = annualTax / periods;                                                        // 2h

        // Step 3: credits. Step 4: extra withholding.
        var afterCredits = Math.Max(0, perPeriod - (decimal)input.W4DependentsCreditCents / periods);
        return Round(afterCredits + input.W4ExtraWithholdingCents);
    }

    /// <summary>
    /// LST: over $10 a year it is split evenly across the year's pay periods, rounded down to the
    /// cent; $10 or less is taken as a lump sum. Never more than the annual amount or the $52 cap.
    /// </summary>
    internal static long LocalServicesTax(PaycheckInput input, int periods, TaxTables tables)
    {
        if (input.IsLstExempt || input.WorkLstAnnualCents <= 0)
        {
            return 0;
        }
        var annual = Math.Min(input.WorkLstAnnualCents, tables.Pennsylvania.LstAnnualCapCents);
        var remaining = Math.Max(0, annual - input.YearToDateLstCents);
        var due = annual <= tables.Pennsylvania.LstLumpSumMaxCents ? annual : annual / periods; // integer division rounds down
        return Math.Min(due, remaining);
    }

    private static long Round(decimal cents) => Money.RoundToCents(cents);

    private static string Percent(decimal rate) => $"{rate * 100:0.##}%";
}

using Ledger.Core.Models;
using Ledger.Core.Payroll;

namespace Ledger.Core.Tests;

public class PayrollTaxCalculatorTests
{
    // MapleBoro resident (1.5%) working in Riverton City (0.5% non-resident, $52 LST).
    private static PaycheckInput Market(long grossCents, PayFrequency frequency = PayFrequency.Weekly) => new()
    {
        GrossCents = grossCents,
        Frequency = frequency,
        ResidentEitBasisPoints = 150,
        WorkNonResidentEitBasisPoints = 50,
        WorkLstAnnualCents = 5200,
    };

    private static PaycheckInput Plain(long grossCents, PayFrequency frequency = PayFrequency.Weekly) => new()
    {
        GrossCents = grossCents,
        Frequency = frequency,
    };

    private static long Line(PaycheckBreakdown b, PaycheckLineKind kind) => b.Amount(kind);

    // ---- Reference data integrity -------------------------------------------------------------

    [Theory]
    [InlineData(FilingStatus.Single, false)]
    [InlineData(FilingStatus.MarriedFilingJointly, false)]
    [InlineData(FilingStatus.HeadOfHousehold, false)]
    [InlineData(FilingStatus.Single, true)]
    [InlineData(FilingStatus.MarriedFilingJointly, true)]
    [InlineData(FilingStatus.HeadOfHousehold, true)]
    public void Irs_tables_are_internally_consistent(FilingStatus status, bool step2)
    {
        // Every row's base must equal the tax at the top of the row before it. This catches any
        // transcription error in tax-2026.json. The IRS rounds halved Step 2 thresholds to whole
        // dollars (e.g. $217,875 ÷ 2 → $108,938), leaving bases a few cents off continuity, so
        // up to $1 is tolerated. A typo would be off by far more.
        var tables = TaxTables.For(2026, out _);
        var rows = (step2 ? tables.Federal.Step2 : tables.Federal.Standard)[status];
        Assert.Equal(0, rows[0].Over);
        for (var i = 1; i < rows.Length; i++)
        {
            var expected = rows[i - 1].Base + (rows[i].Over - rows[i - 1].Over) * rows[i - 1].Rate;
            Assert.True(Math.Abs(expected - rows[i].Base) <= 100, $"{status} step2={step2} row {i}: expected base {expected}, found {rows[i].Base}");
            Assert.True(rows[i].Rate > rows[i - 1].Rate || rows[i - 1].Rate == 0);
        }
        Assert.Equal(0.37m, rows[^1].Rate);
    }

    [Fact]
    public void Missing_year_falls_back_to_latest_earlier_tables_and_says_so()
    {
        var tables = TaxTables.For(2027, out var exact);
        Assert.Equal(2026, tables.Year);
        Assert.False(exact);
        Assert.False(PayrollTaxCalculator.Calculate(Plain(10000), 2027).TaxTablesExact);
    }

    // ---- Worked examples ----------------------------------------------------------------------

    [Fact]
    public void Plan_example_900_biweekly_single()
    {
        var b = PayrollTaxCalculator.Calculate(new PaycheckInput
        {
            GrossCents = 90000,
            Frequency = PayFrequency.Biweekly,
            ResidentEitBasisPoints = 150,
        }, 2026);

        Assert.Equal(2808, Line(b, PaycheckLineKind.FederalIncomeTax));   // ($23,400 − $8,600 − $7,500) × 10% ÷ 26
        Assert.Equal(5580, Line(b, PaycheckLineKind.SocialSecurity));
        Assert.Equal(1305, Line(b, PaycheckLineKind.Medicare));
        Assert.Equal(2763, Line(b, PaycheckLineKind.PennsylvaniaIncomeTax));
        Assert.Equal(63, Line(b, PaycheckLineKind.PennsylvaniaUnemployment));
        Assert.Equal(1350, Line(b, PaycheckLineKind.LocalEarnedIncomeTax));
        Assert.Equal(76131, b.NetCents);
    }

    [Fact]
    public void First_full_market_week_14_hours_at_13_50()
    {
        var b = PayrollTaxCalculator.Calculate(Market(18900), 2026);

        Assert.Equal(0, Line(b, PaycheckLineKind.FederalIncomeTax));      // $9,828/yr − $8,600 is under the 0% band
        Assert.Equal(1172, Line(b, PaycheckLineKind.SocialSecurity));     // 11.718
        Assert.Equal(274, Line(b, PaycheckLineKind.Medicare));            // 2.7405
        Assert.Equal(580, Line(b, PaycheckLineKind.PennsylvaniaIncomeTax)); // 5.8023
        Assert.Equal(13, Line(b, PaycheckLineKind.PennsylvaniaUnemployment)); // 0.1323
        Assert.Equal(284, Line(b, PaycheckLineKind.LocalEarnedIncomeTax)); // 2.835, half away from zero
        Assert.Equal(100, Line(b, PaycheckLineKind.LocalServicesTax));    // $52 ÷ 52
        Assert.Equal(16477, b.NetCents);
    }

    [Fact]
    public void Outside_pennsylvania_only_federal_and_fica_are_estimated()
    {
        var pa = PayrollTaxCalculator.Calculate(Market(100_000), 2026);
        var elsewhere = PayrollTaxCalculator.Calculate(Market(100_000) with { PennsylvaniaTaxes = false }, 2026);

        Assert.Equal([PaycheckLineKind.FederalIncomeTax, PaycheckLineKind.SocialSecurity, PaycheckLineKind.Medicare],
            elsewhere.Lines.Select(l => l.Kind));
        foreach (var kind in elsewhere.Lines.Select(l => l.Kind))
        {
            Assert.Equal(Line(pa, kind), Line(elsewhere, kind));
        }
        Assert.True(elsewhere.NetCents > pa.NetCents);
    }

    [Fact]
    public void Federal_single_weekly_1000_is_in_the_12_percent_band()
    {
        // $52,000 − $8,600 = $43,400 → $1,240 + 12% × ($43,400 − $19,900) = $4,060 ÷ 52
        Assert.Equal(7808, Line(PayrollTaxCalculator.Calculate(Plain(100000), 2026), PaycheckLineKind.FederalIncomeTax));
    }

    [Fact]
    public void Federal_multiple_jobs_box_uses_the_step2_table_and_no_step1g_deduction()
    {
        // $52,000 → $2,900 + 22% × ($52,000 − $33,250) = $7,025 ÷ 52
        var b = PayrollTaxCalculator.Calculate(Plain(100000) with { W4MultipleJobs = true }, 2026);
        Assert.Equal(13510, Line(b, PaycheckLineKind.FederalIncomeTax));
    }

    [Fact]
    public void Federal_married_filing_jointly()
    {
        // $52,000 − $12,900 = $39,100 → 10% × ($39,100 − $19,300) = $1,980 ÷ 52
        var b = PayrollTaxCalculator.Calculate(Plain(100000) with { FilingStatus = FilingStatus.MarriedFilingJointly }, 2026);
        Assert.Equal(3808, Line(b, PaycheckLineKind.FederalIncomeTax));
    }

    [Fact]
    public void Federal_credits_never_go_below_zero_and_extra_withholding_is_added_after()
    {
        var b = PayrollTaxCalculator.Calculate(Plain(100000) with
        {
            W4DependentsCreditCents = 1_000_000,   // $10,000 a year: far more than the tax
            W4ExtraWithholdingCents = 2500,
        }, 2026);
        Assert.Equal(2500, Line(b, PaycheckLineKind.FederalIncomeTax));
    }

    [Fact]
    public void Federal_step4b_deductions_reduce_withholding()
    {
        var baseline = Line(PayrollTaxCalculator.Calculate(Plain(100000), 2026), PaycheckLineKind.FederalIncomeTax);
        var withOvertimeDeduction = Line(PayrollTaxCalculator.Calculate(Plain(100000) with { W4DeductionsCents = 520_000 }, 2026), PaycheckLineKind.FederalIncomeTax);
        Assert.Equal(baseline - 1200, withOvertimeDeduction); // $5,200 × 12% ÷ 52 = $12.00
    }

    // ---- FICA ---------------------------------------------------------------------------------

    [Fact]
    public void Social_security_stops_at_the_wage_base_mid_paycheck()
    {
        var b = PayrollTaxCalculator.Calculate(Plain(100000) with { YearToDateFicaWagesCents = 18_400_000 }, 2026);
        Assert.Equal(3100, Line(b, PaycheckLineKind.SocialSecurity)); // only $500 of room left
        Assert.Equal(0, Line(PayrollTaxCalculator.Calculate(Plain(100000) with { YearToDateFicaWagesCents = 18_450_000 }, 2026), PaycheckLineKind.SocialSecurity));
    }

    [Fact]
    public void Additional_medicare_applies_only_above_200k()
    {
        var b = PayrollTaxCalculator.Calculate(Plain(100000) with { YearToDateFicaWagesCents = 19_950_000 }, 2026);
        Assert.Equal(1450 + 450, Line(b, PaycheckLineKind.Medicare)); // 1.45% of $1,000 + 0.9% of $500
    }

    // ---- Pre-tax deductions -------------------------------------------------------------------

    [Fact]
    public void Retirement_deferral_lowers_federal_tax_only()
    {
        var b = PayrollTaxCalculator.Calculate(Plain(100000) with { PreTaxRetirementCents = 10000 }, 2026);
        // Federal on $900: ($46,800 − $8,600 − $19,900) × 12% + $1,240 = $3,436 ÷ 52
        Assert.Equal(6608, Line(b, PaycheckLineKind.FederalIncomeTax));
        Assert.Equal(6200, Line(b, PaycheckLineKind.SocialSecurity));          // full $1,000
        Assert.Equal(3070, Line(b, PaycheckLineKind.PennsylvaniaIncomeTax));   // PA taxes 401(k) deferrals
        Assert.Equal(10000, Line(b, PaycheckLineKind.PreTaxRetirement));
    }

    [Fact]
    public void Section_125_health_lowers_federal_fica_and_pa_but_not_unemployment()
    {
        var b = PayrollTaxCalculator.Calculate(Plain(100000) with { PreTaxHealthCents = 10000 }, 2026);
        Assert.Equal(5580, Line(b, PaycheckLineKind.SocialSecurity));        // 6.2% of $900
        Assert.Equal(1305, Line(b, PaycheckLineKind.Medicare));
        Assert.Equal(2763, Line(b, PaycheckLineKind.PennsylvaniaIncomeTax));
        Assert.Equal(70, Line(b, PaycheckLineKind.PennsylvaniaUnemployment)); // on full gross
    }

    [Fact]
    public void Net_is_gross_minus_every_line()
    {
        var b = PayrollTaxCalculator.Calculate(Market(26325) with { PreTaxHealthCents = 1500, PreTaxRetirementCents = 1000 }, 2026);
        Assert.Equal(b.GrossCents - b.Lines.Sum(l => l.AmountCents), b.NetCents);
        Assert.True(b.NetCents > 0);
    }

    // ---- Local taxes --------------------------------------------------------------------------

    [Theory]
    [InlineData(150, 50, 150)]   // MapleBoro home rate beats Riverton's non-resident rate
    [InlineData(50, 100, 100)]   // work rate wins when higher (Act 32)
    [InlineData(0, 0, 0)]
    public void Local_eit_is_the_higher_of_home_and_work(int resident, int work, int expectedBps)
    {
        var b = PayrollTaxCalculator.Calculate(Plain(100000) with { ResidentEitBasisPoints = resident, WorkNonResidentEitBasisPoints = work }, 2026);
        Assert.Equal(100000 * expectedBps / 10000, Line(b, PaycheckLineKind.LocalEarnedIncomeTax));
    }

    [Theory]
    [InlineData(PayFrequency.Weekly, 100)]
    [InlineData(PayFrequency.Biweekly, 200)]
    [InlineData(PayFrequency.Semimonthly, 216)]   // $52 ÷ 24 = 2.1666 → rounded down
    [InlineData(PayFrequency.Monthly, 433)]       // DCED's own example: $4.33
    public void Lst_over_ten_dollars_is_split_evenly_and_rounded_down(PayFrequency frequency, long expected)
    {
        Assert.Equal(expected, Line(PayrollTaxCalculator.Calculate(Market(50000, frequency), 2026), PaycheckLineKind.LocalServicesTax));
    }

    [Fact]
    public void Lst_of_ten_dollars_or_less_is_one_lump_sum()
    {
        var first = PayrollTaxCalculator.Calculate(Plain(50000) with { WorkLstAnnualCents = 1000 }, 2026);
        var later = PayrollTaxCalculator.Calculate(Plain(50000) with { WorkLstAnnualCents = 1000, YearToDateLstCents = 1000 }, 2026);
        Assert.Equal(1000, Line(first, PaycheckLineKind.LocalServicesTax));
        Assert.Equal(0, Line(later, PaycheckLineKind.LocalServicesTax));
    }

    [Fact]
    public void Lst_never_exceeds_the_annual_amount_and_exemption_removes_it()
    {
        Assert.Equal(50, Line(PayrollTaxCalculator.Calculate(Market(50000) with { YearToDateLstCents = 5150 }, 2026), PaycheckLineKind.LocalServicesTax));
        Assert.Equal(0, Line(PayrollTaxCalculator.Calculate(Market(50000) with { YearToDateLstCents = 5200 }, 2026), PaycheckLineKind.LocalServicesTax));
        Assert.Equal(0, Line(PayrollTaxCalculator.Calculate(Market(50000) with { IsLstExempt = true }, 2026), PaycheckLineKind.LocalServicesTax));
        Assert.DoesNotContain(PayrollTaxCalculator.Calculate(Plain(50000), 2026).Lines, l => l.Kind == PaycheckLineKind.LocalServicesTax);
    }

    [Fact]
    public void Zero_gross_withholds_nothing_but_lst()
    {
        var b = PayrollTaxCalculator.Calculate(Market(0), 2026);
        Assert.Equal(-100, b.NetCents); // only the flat LST; the forecast never creates empty paychecks
        Assert.All(b.Lines.Where(l => l.Kind != PaycheckLineKind.LocalServicesTax), l => Assert.Equal(0, l.AmountCents));
    }

    [Fact]
    public void Negative_gross_is_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PayrollTaxCalculator.Calculate(Plain(-1), 2026));
}

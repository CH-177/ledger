using Ledger.Core.Models;
using Ledger.Core.Services;

namespace Ledger.Core.Payroll;

public enum StubLineStatus
{
    Matches,
    Differs,
    NotEntered,
}

/// <summary>One line of the comparison: Ledger's figure next to the paystub's.</summary>
public sealed record StubLine(string Label, long? LedgerCents, long? PaystubCents, StubLineStatus Status)
{
    public long? DifferenceCents => LedgerCents is { } l && PaystubCents is { } p ? p - l : null;
}

/// <summary>A payday the paystub shows, which the user can confirm for the job.</summary>
public sealed record PaydaySuggestion(DateOnly PeriodStart, int OffsetDays, DayOfWeek PayWeekday);

public sealed record PaystubReport(
    IReadOnlyList<StubLine> Lines,
    IReadOnlyList<string> Hints,
    PaydaySuggestion? Payday,
    bool AllMatch);

/// <summary>
/// Compares a real paystub with Ledger's estimate and explains the likely cause of each difference.
/// Taxes are compared against Ledger's calculation re-run on the paystub's own gross, so a
/// difference in hours doesn't make every tax look wrong: each difference points at its real
/// cause (hours, rate, W-4, benefits, local rate).
/// </summary>
public static class PaystubComparison
{
    /// <summary>Per-line rounding differences up to this are treated as a match.</summary>
    public const long ToleranceCents = 2;

    public static PaystubReport Compare(PredictedPaycheck estimate, Paystub stub)
    {
        var year = estimate.Period.PayDate.Year;
        var onStubGross = estimate.Input is { } input
            ? PayrollTaxCalculator.Calculate(input with { GrossCents = stub.GrossCents }, year)
            : estimate.Breakdown;
        var lines = new List<StubLine>();
        var hints = new List<string>();

        static StubLineStatus StatusOf(long? ledger, long? paystub) =>
            paystub is null || ledger is null ? StubLineStatus.NotEntered
            : Math.Abs(paystub.Value - ledger.Value) <= ToleranceCents ? StubLineStatus.Matches
            : StubLineStatus.Differs;

        // Hours and gross: against the estimate itself.
        if (stub.PaidMinutes is { } minutes)
        {
            lines.Add(new("Paid hours (minutes)", estimate.TotalMinutes, minutes,
                minutes == estimate.TotalMinutes ? StubLineStatus.Matches : StubLineStatus.Differs));
        }
        var grossStatus = StatusOf(estimate.GrossCents, stub.GrossCents);
        lines.Add(new("Gross pay", estimate.GrossCents, stub.GrossCents, grossStatus));
        if (grossStatus == StubLineStatus.Differs)
        {
            if (stub.PaidMinutes is { } m && m != estimate.TotalMinutes)
            {
                hints.Add($"Your paystub shows {HoursText(m)} paid; Ledger has {HoursText(estimate.TotalMinutes)} for this pay period. " +
                          "Update that period's shifts (hours, breaks, or whether they were worked).");
            }
            else if (stub.PaidMinutes is { } same && same > 0)
            {
                var implied = Money.RoundToCents(stub.GrossCents * 60m / same);
                hints.Add($"Same hours, but the pay works out to {Money.Format(implied)} an hour. Check the job's pay rate and overtime.");
            }
            else
            {
                hints.Add("Gross pay differs. Enter the paystub's hours to see whether it's the hours or the pay rate.");
            }
        }

        // Taxes: against Ledger's calculation for the paystub's gross.
        void Tax(string label, PaycheckLineKind kind, long? paystub, Func<long, long, string?> hint)
        {
            var expected = onStubGross.Amount(kind);
            var status = StatusOf(expected, paystub);
            lines.Add(new(label, expected, paystub, status));
            if (status == StubLineStatus.Differs && hint(expected, paystub!.Value) is { } text)
            {
                hints.Add(text);
            }
        }

        var health = onStubGross.Amount(PaycheckLineKind.PreTaxHealth);
        var ficaBase = stub.GrossCents - health;
        var paBase = stub.GrossCents - health;

        Tax("Federal income tax", PaycheckLineKind.FederalIncomeTax, stub.FederalCents, (e, p) =>
            $"Federal withholding is {Money.Format(p)}; Ledger expects {Money.Format(e)} for this pay. " +
            "Check the job's W-4 settings: filing status, the multiple-jobs box (Step 2), and extra withholding (Step 4c).");
        Tax("Social Security", PaycheckLineKind.SocialSecurity, stub.SocialSecurityCents, (e, p) =>
            BaseHint("Social Security", e, p, ficaBase));
        Tax("Medicare", PaycheckLineKind.Medicare, stub.MedicareCents, (e, p) =>
            lines.Last(l => l.Label == "Social Security").Status == StubLineStatus.Differs ? null : BaseHint("Medicare", e, p, ficaBase));
        Tax("PA income tax", PaycheckLineKind.PennsylvaniaIncomeTax, stub.StateCents, (e, p) =>
            BaseHint("PA income tax", e, p, paBase));
        Tax("PA unemployment", PaycheckLineKind.PennsylvaniaUnemployment, stub.StateUnemploymentCents, (e, p) =>
            $"PA unemployment is {Money.Format(p)} vs {Money.Format(e)} expected (0.07% of gross).");
        Tax("Local earned income tax", PaycheckLineKind.LocalEarnedIncomeTax, stub.LocalEitCents, (e, p) =>
        {
            if (paBase <= 0) return null;
            var impliedPercent = p * 100m / paBase;
            var usedBps = estimate.Input is { } i ? Math.Max(i.ResidentEitBasisPoints, i.WorkNonResidentEitBasisPoints) : 0;
            return $"Local tax on your paystub is {impliedPercent:0.##}% of wages; Ledger uses {usedBps / 100m:0.##}% " +
                   "(the higher of your home rate and your work location's). Update Settings → Local tax, or the job's work-location rate.";
        });
        Tax("Local services tax", PaycheckLineKind.LocalServicesTax, stub.LocalServicesTaxCents, (e, p) =>
            $"Local services tax is {Money.Format(p)} on this paystub; Ledger expected {Money.Format(e)}. " +
            "Employers may take the yearly amount in one go or on their own schedule. Ledger spreads it evenly.");

        // Deductions Ledger doesn't know about: what's left once taxes and known deductions are accounted for.
        var taxesEntered = new long?[] { stub.FederalCents, stub.SocialSecurityCents, stub.MedicareCents, stub.StateCents, stub.LocalEitCents };
        if (taxesEntered.All(t => t is not null))
        {
            var known = onStubGross.Lines.Where(l => l.Kind is PaycheckLineKind.PreTaxHealth or PaycheckLineKind.PreTaxRetirement or PaycheckLineKind.AfterTax)
                .Sum(l => l.AmountCents);
            var other = stub.GrossCents - stub.NetCents - taxesEntered.Sum(t => t!.Value)
                        - (stub.StateUnemploymentCents ?? onStubGross.Amount(PaycheckLineKind.PennsylvaniaUnemployment))
                        - (stub.LocalServicesTaxCents ?? onStubGross.Amount(PaycheckLineKind.LocalServicesTax))
                        - known;
            if (Math.Abs(other) > ToleranceCents * 3)
            {
                hints.Add(other > 0
                    ? $"{Money.Format(other)} was taken for something Ledger doesn't know about. Add it under the job's deductions (for example a benefit or dues)."
                    : $"Your paystub keeps {Money.Format(-other)} more than Ledger's deductions allow. A deduction in Ledger may have ended or changed.");
            }
        }

        var netStatus = StatusOf(onStubGross.NetCents, stub.NetCents);
        lines.Add(new("Take-home pay", onStubGross.NetCents, stub.NetCents, netStatus));

        // Payday and pay period.
        PaydaySuggestion? payday = null;
        var stubEnd = stub.StubPeriodEnd ?? estimate.Period.End;
        var offset = stub.PayDate.DayNumber - stubEnd.DayNumber;
        var periodDiffers = stub.StubPeriodStart is { } ps && ps != estimate.Period.Start;
        if (offset is >= 0 and <= 31 && (!estimate.IsPaydayConfirmed || stub.PayDate != estimate.Period.PayDate || periodDiffers))
        {
            payday = new PaydaySuggestion(stub.StubPeriodStart ?? estimate.Period.Start, offset, stub.PayDate.DayOfWeek);
        }
        if (periodDiffers)
        {
            hints.Add($"The paystub's pay period starts {stub.StubPeriodStart:MMM d}; Ledger assumed {estimate.Period.Start:MMM d}. " +
                      "Confirming the payday below lines Ledger up with it.");
        }

        var allMatch = lines.All(l => l.Status != StubLineStatus.Differs);
        if (allMatch)
        {
            hints.Insert(0, "Your paystub matches Ledger's estimate for this pay.");
        }
        return new PaystubReport(lines, hints, payday, allMatch);
    }

    /// <summary>When a tax was taken on less than gross, a pre-tax benefit is the usual reason.</summary>
    private static string BaseHint(string tax, long expected, long paystub, long expectedBase)
    {
        if (expected <= 0)
        {
            return $"{tax} is {Money.Format(paystub)} on the paystub, but Ledger expected none.";
        }
        var impliedBase = Money.RoundToCents((decimal)paystub * expectedBase / expected);
        var gap = expectedBase - impliedBase;
        return gap > 100
            ? $"{tax} was taken on about {Money.Format(impliedBase)}, {Money.Format(gap)} less than Ledger expected. " +
              "That usually means a pre-tax benefit (health, dental or vision). Add it under the job's deductions."
            : $"{tax} is {Money.Format(paystub)}; Ledger expected {Money.Format(expected)}. Check year-to-date totals on the stub.";
    }

    private static string HoursText(int minutes) => minutes % 60 == 0 ? $"{minutes / 60} h" : $"{minutes / 60m:0.##} h";
}

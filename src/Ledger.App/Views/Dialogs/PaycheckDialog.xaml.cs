using System.Globalization;
using Ledger.App.ViewModels;
using Ledger.Core;
using Ledger.Core.Payroll;
using Ledger.Core.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI.Text;

namespace Ledger.App.Views.Dialogs;

/// <summary>One line of the paystub-style breakdown.</summary>
/// <remarks>A class with read-only properties: XAML's type info can't handle init-only record setters.</remarks>
public sealed class PaycheckDetailRow(string label, string amount, string explanation, bool isTotal)
{
    public string Label { get; } = label;
    public string Amount { get; } = amount;
    public string Explanation { get; } = explanation;
    public bool IsTotal { get; } = isTotal;
    public bool HasExplanation => Explanation.Length > 0;
    public FontWeight Weight => IsTotal ? FontWeights.SemiBold : FontWeights.Normal;
    public Thickness Border => IsTotal ? new Thickness(0, 1, 0, 0) : new Thickness(0);
}

/// <summary>
/// Shows how a predicted paycheck is calculated. The primary button records the real
/// paycheck, which the caller handles when the dialog returns Primary.
/// </summary>
public sealed partial class PaycheckDialog : ContentDialog
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");

    public PaycheckRowViewModel Row { get; }
    public string TitleText { get; }
    public string BadgeText { get; }
    public string PeriodText { get; }
    public string PrimaryText { get; }

    /// <summary>Enter or review the real paystub for this paycheck.</summary>
    public string SecondaryText { get; }
    public List<PaycheckDetailRow> Rows { get; } = [];
    public List<string> Notes { get; } = [];

    public PaycheckDialog(PaycheckRowViewModel row)
    {
        Row = row;
        var p = row.Paycheck;
        TitleText = $"{p.JobName} paycheck, {p.Period.PayDate.ToString("dddd, MMM d", Culture)}";
        BadgeText = row.IsRecorded ? "Estimate (real paycheck recorded)" : "Estimate";
        PeriodText = $"For work {p.Period.Start.ToString("MMM d", Culture)} – {p.Period.End.ToString("MMM d, yyyy", Culture)}, " +
                     $"{p.ShiftCount} {(p.ShiftCount == 1 ? "shift" : "shifts")}";
        PrimaryText = row.IsRecorded ? "" : "Record this paycheck";
        SecondaryText = p.Paystub is null ? "Enter paystub…" : "Paystub…";

        var rate = Money.Format(p.HourlyRateCents);
        Rows.Add(new($"Regular pay: {HoursFormat.Of(p.RegularMinutes)} × {rate}", Money.Format(p.RegularPayCents), "", false));
        if (p.OvertimeMinutes > 0)
        {
            Rows.Add(new($"Overtime: {HoursFormat.Of(p.OvertimeMinutes)} × {rate} × {GrossPayCalculator.OvertimeMultiplier:0.#}", Money.Format(p.OvertimePayCents), "Hours over 40 in a workweek.", false));
        }
        Rows.Add(new("Gross pay", Money.Format(p.GrossCents), "", true));
        foreach (var line in p.Breakdown.Lines)
        {
            Rows.Add(new(line.Label, Money.Format(-line.AmountCents), line.Explanation, false));
        }
        Rows.Add(new("Estimated take-home", Money.Format(p.NetCents), "", true));
        if (p.Recorded is { } r)
        {
            Rows.Add(new("Actually deposited", Money.Format(r.ActualCents),
                $"{Money.Format(r.ActualCents - p.NetCents, MoneySign.Always)} compared with the estimate.", true));
        }

        if (!p.IsPaydayConfirmed)
        {
            Notes.Add("Payday is assumed. Confirm it in the job's settings once you have a paystub.");
        }
        if (p.HasUnconfirmedShifts)
        {
            Notes.Add("Includes shifts that are only scheduled so far. If hours change, so will this estimate.");
        }
        if (p.OvertimeMinutes > 0)
        {
            Notes.Add("The 2026 federal overtime deduction lowers your tax when you file, not this paycheck, unless you claim it on a new W-4 (Step 4b).");
        }
        if (!p.Breakdown.TaxTablesExact)
        {
            Notes.Add($"Tax tables for {p.Period.PayDate.Year} aren't in Ledger yet, so {p.Breakdown.TaxYear} rates were used.");
        }
        Notes.Add("This estimates what your employer withholds, not the tax you'll owe for the year. Your real paystub may differ by a few cents.");

        InitializeComponent();
    }
}

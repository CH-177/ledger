using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Ledger.Core;
using Ledger.Core.Models;
using Ledger.Core.Payroll;
using Ledger.Core.Services;
using Microsoft.Extensions.Logging;

namespace Ledger.App.ViewModels;

/// <summary>One comparison line, ready to show.</summary>
public sealed record StubLineViewModel(string Label, string LedgerText, string PaystubText, string DifferenceText, bool Differs, bool NotEntered)
{
    public bool Matches => !Differs && !NotEntered;
    public override string ToString() =>
        $"{Label}: Ledger {LedgerText}, paystub {PaystubText}" + (Differs ? $", differs by {DifferenceText}" : Matches ? ", matches" : ", not entered");
}

/// <summary>
/// Entering a paystub and comparing it with Ledger's estimate, line by line, as you type. Saving
/// stores the paystub (and, if chosen, confirms the job's payday). It never changes the estimate or
/// any transaction.
/// </summary>
public sealed partial class PaystubViewModel(PaystubService paystubs, ILogger<PaystubViewModel> logger) : ObservableObject
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");
    private PredictedPaycheck? _estimate;
    private PaystubReport? _report;
    private bool _loading;

    public ObservableCollection<StubLineViewModel> Lines { get; } = [];
    public ObservableCollection<string> Hints { get; } = [];

    public string Title { get; private set; } = "";
    public string Intro { get; private set; } = "";

    [ObservableProperty] public partial DateTimeOffset? PayDate { get; set; }
    [ObservableProperty] public partial DateTimeOffset? PeriodStart { get; set; }
    [ObservableProperty] public partial DateTimeOffset? PeriodEnd { get; set; }
    [ObservableProperty] public partial string HoursText { get; set; } = "";
    [ObservableProperty] public partial string GrossText { get; set; } = "";
    [ObservableProperty] public partial string FederalText { get; set; } = "";
    [ObservableProperty] public partial string SocialSecurityText { get; set; } = "";
    [ObservableProperty] public partial string MedicareText { get; set; } = "";
    [ObservableProperty] public partial string StateText { get; set; } = "";
    [ObservableProperty] public partial string UnemploymentText { get; set; } = "";
    [ObservableProperty] public partial string LocalText { get; set; } = "";
    [ObservableProperty] public partial string LstText { get; set; } = "";
    [ObservableProperty] public partial string NetText { get; set; } = "";

    [ObservableProperty] public partial bool HasComparison { get; set; }
    [ObservableProperty] public partial string SummaryText { get; set; } = "";
    [ObservableProperty] public partial bool HasPayday { get; set; }
    [ObservableProperty] public partial string PaydayText { get; set; } = "";
    [ObservableProperty] public partial bool ConfirmPayday { get; set; } = true;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty] public partial bool IsSaving { get; set; }
    public bool HasError => ErrorMessage is not null;

    public void Load(PredictedPaycheck estimate)
    {
        _loading = true;
        _estimate = estimate;
        Title = $"Paystub for {estimate.Period.PayDate.ToString("MMM d", Culture)}";
        Intro = $"Type the numbers from your {estimate.JobName} paystub. Only gross pay and take-home are needed; each extra line you add sharpens the check.";
        var s = estimate.Paystub;
        PayDate = Offset(s?.PayDate ?? estimate.Period.PayDate);
        PeriodStart = Offset(s?.StubPeriodStart ?? estimate.Period.Start);
        PeriodEnd = Offset(s?.StubPeriodEnd ?? estimate.Period.End);
        HoursText = s?.PaidMinutes is { } m ? (m / 60m).ToString("0.##", CultureInfo.InvariantCulture) : "";
        GrossText = Edit(s?.GrossCents);
        FederalText = Edit(s?.FederalCents);
        SocialSecurityText = Edit(s?.SocialSecurityCents);
        MedicareText = Edit(s?.MedicareCents);
        StateText = Edit(s?.StateCents);
        UnemploymentText = Edit(s?.StateUnemploymentCents);
        LocalText = Edit(s?.LocalEitCents);
        LstText = Edit(s?.LocalServicesTaxCents);
        NetText = Edit(s?.NetCents);
        _loading = false;
        Refresh();
    }

    /// <summary>Any edit re-runs the comparison.</summary>
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_loading && e.PropertyName is not (nameof(Lines) or nameof(HasComparison) or nameof(SummaryText) or nameof(HasPayday)
                or nameof(PaydayText) or nameof(ConfirmPayday) or nameof(ErrorMessage) or nameof(HasError) or nameof(IsSaving)))
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        Lines.Clear();
        Hints.Clear();
        _report = null;
        if (_estimate is null || TryRead(out var stub, out _) is false)
        {
            HasComparison = false;
            HasPayday = false;
            SummaryText = "Enter gross pay and take-home to compare.";
            return;
        }

        _report = PaystubComparison.Compare(_estimate, stub);
        foreach (var l in _report.Lines)
        {
            var isHours = l.Label.StartsWith("Paid hours", StringComparison.Ordinal);
            string Show(long? v) => v is null ? "—" : isHours ? $"{v.Value / 60m:0.##} h" : Money.Format(v.Value);
            Lines.Add(new StubLineViewModel(isHours ? "Paid hours" : l.Label, Show(l.LedgerCents), Show(l.PaystubCents),
                l.DifferenceCents is { } d ? (isHours ? $"{d / 60m:+0.##;-0.##} h" : Money.Format(d, MoneySign.Always)) : "",
                l.Status == StubLineStatus.Differs, l.Status == StubLineStatus.NotEntered));
        }
        foreach (var h in _report.Hints)
        {
            Hints.Add(h);
        }
        HasComparison = true;
        SummaryText = _report.AllMatch
            ? "Everything you entered matches."
            : $"{_report.Lines.Count(l => l.Status == StubLineStatus.Differs)} line(s) differ. Ledger's column is its calculation for the paystub's gross pay.";
        HasPayday = _report.Payday is not null;
        PaydayText = _report.Payday is { } p
            ? $"Set this job's payday: every {p.PayWeekday}, {p.OffsetDays} day{(p.OffsetDays == 1 ? "" : "s")} after the pay period ends, with periods lined up to this paystub."
            : "";
    }

    /// <summary>Reads the form. Gross and take-home are required; blank tax lines are simply not compared.</summary>
    private bool TryRead(out Paystub stub, out string? problem)
    {
        stub = new Paystub();
        problem = null;
        if (!Money.TryParse(GrossText, out var gross) || gross <= 0)
        {
            problem = "Enter the gross pay from the paystub.";
            return false;
        }
        if (!Money.TryParse(NetText, out var net) || net < 0)
        {
            problem = "Enter the take-home (net) pay from the paystub.";
            return false;
        }
        if (PayDate is not { } payDate)
        {
            problem = "Choose the pay date on the paystub.";
            return false;
        }
        int? minutes = null;
        if (!string.IsNullOrWhiteSpace(HoursText))
        {
            if (!decimal.TryParse(HoursText, NumberStyles.Number, CultureInfo.InvariantCulture, out var hours) || hours < 0)
            {
                problem = "Enter paid hours as a number, like 13.5.";
                return false;
            }
            minutes = (int)Math.Round(hours * 60);
        }

        long? Optional(string text, string label, ref string? error)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (Money.TryParse(text, out var cents) && cents >= 0) return cents;
            error ??= $"Check the {label} amount.";
            return null;
        }

        string? error = null;
        stub = new Paystub
        {
            JobId = _estimate!.JobId,
            PeriodStart = _estimate.Period.Start,
            PayDate = DateOnly.FromDateTime(payDate.Date),
            StubPeriodStart = PeriodStart is { } ps ? DateOnly.FromDateTime(ps.Date) : null,
            StubPeriodEnd = PeriodEnd is { } pe ? DateOnly.FromDateTime(pe.Date) : null,
            PaidMinutes = minutes,
            GrossCents = gross,
            NetCents = net,
            FederalCents = Optional(FederalText, "federal", ref error),
            SocialSecurityCents = Optional(SocialSecurityText, "Social Security", ref error),
            MedicareCents = Optional(MedicareText, "Medicare", ref error),
            StateCents = Optional(StateText, "PA income tax", ref error),
            StateUnemploymentCents = Optional(UnemploymentText, "PA unemployment", ref error),
            LocalEitCents = Optional(LocalText, "local tax", ref error),
            LocalServicesTaxCents = Optional(LstText, "local services tax", ref error),
        };
        problem = error;
        return error is null;
    }

    public async Task<bool> SaveAsync()
    {
        ErrorMessage = null;
        if (!TryRead(out var s, out var problem))
        {
            ErrorMessage = problem;
            return false;
        }
        IsSaving = true;
        try
        {
            await paystubs.SaveAsync(s.JobId, s.PeriodStart, new PaystubInput(
                s.PayDate, s.GrossCents, s.NetCents, s.PaidMinutes, s.StubPeriodStart, s.StubPeriodEnd,
                s.FederalCents, s.SocialSecurityCents, s.MedicareCents, s.StateCents, s.StateUnemploymentCents, s.LocalEitCents, s.LocalServicesTaxCents));
            if (ConfirmPayday && _report?.Payday is { } payday)
            {
                await paystubs.ConfirmPaydayAsync(s.JobId, payday);
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
            logger.LogError(ex, "Failed to save paystub");
            ErrorMessage = "Something went wrong while saving the paystub. Your existing data has not been changed.";
            return false;
        }
        finally
        {
            IsSaving = false;
        }
    }

    private static DateTimeOffset Offset(DateOnly d) => new(d.ToDateTime(TimeOnly.MinValue));
    private static string Edit(long? cents) => cents is { } c ? Money.FormatForEditing(c) : "";
}

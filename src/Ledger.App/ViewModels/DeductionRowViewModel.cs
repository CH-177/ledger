using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Ledger.Core;
using Ledger.Core.Models;
using Ledger.Core.Services;

namespace Ledger.App.ViewModels;

/// <summary>One paycheck deduction being edited in the job dialog.</summary>
public sealed partial class DeductionRowViewModel : ObservableObject
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");
    public static readonly DeductionKind[] KindOrder = [DeductionKind.PreTaxHealth, DeductionKind.PreTaxRetirement, DeductionKind.AfterTax];

    public static IReadOnlyList<string> KindOptions { get; } =
    [
        "Health, dental, vision (pre-tax)",
        "Retirement 401(k) (pre-tax)",
        "After-tax (Roth, dues…)",
    ];

    public int? Id { get; }
    public DateOnly? ExistingStart { get; }

    [ObservableProperty] public partial string Name { get; set; } = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanBePercent), nameof(AmountHeader))]
    public partial int KindIndex { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(AmountHeader))]
    public partial bool IsPercent { get; set; }

    [ObservableProperty] public partial string AmountText { get; set; } = "";
    [ObservableProperty] public partial DateTimeOffset? StartDate { get; set; }

    public DeductionKind Kind => KindOrder[Math.Clamp(KindIndex, 0, KindOrder.Length - 1)];

    /// <summary>Section 125 benefits are fixed premiums; retirement and after-tax can be a share of pay.</summary>
    public bool CanBePercent => Kind != DeductionKind.PreTaxHealth;
    public string AmountHeader => IsPercent && CanBePercent ? "% of gross pay" : "Per paycheck";
    public bool IsNew => Id is null;
    public bool IsExistingRow => !IsNew;
    public string StartedText => ExistingStart is { } d ? $"Since {d.ToString("MMM d, yyyy", Culture)}. Changes apply from today." : "";
    public string RemoveName => $"Remove {(string.IsNullOrWhiteSpace(Name) ? "this deduction" : Name)}";

    public DeductionRowViewModel(DateOnly today) => StartDate = new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue));

    public DeductionRowViewModel(JobDeduction d)
    {
        Id = d.Id;
        ExistingStart = d.StartDate;
        Name = d.Name;
        KindIndex = Array.IndexOf(KindOrder, d.Kind);
        IsPercent = d.PercentBasisPoints is not null;
        AmountText = d.PercentBasisPoints is { } bps
            ? (bps / 100m).ToString("0.##", CultureInfo.InvariantCulture)
            : Money.FormatForEditing(d.AmountCents);
        StartDate = new DateTimeOffset(d.StartDate.ToDateTime(TimeOnly.MinValue));
    }

    /// <summary>Reads the row. Returns an error message in words instead when something is missing.</summary>
    public (DeductionInput? Input, string? Error) ToInput()
    {
        var name = string.IsNullOrWhiteSpace(Name) ? "this deduction" : Name.Trim();
        if (string.IsNullOrWhiteSpace(Name))
        {
            return (null, "Give each deduction a name, like “Dental” or “401(k)”.");
        }
        var start = StartDate is { } s ? DateOnly.FromDateTime(s.Date) : ExistingStart ?? DateOnly.FromDateTime(DateTime.Today);
        if (IsPercent && CanBePercent)
        {
            if (!decimal.TryParse(AmountText.Trim().TrimEnd('%'), NumberStyles.Number, CultureInfo.InvariantCulture, out var percent)
                || percent <= 0 || percent > 100)
            {
                return (null, $"Enter a percentage for “{name}”, like 5.");
            }
            return (new DeductionInput(Id, Name, Kind, 0, (int)Math.Round(percent * 100), start), null);
        }
        if (!Money.TryParse(AmountText, out var cents) || cents <= 0)
        {
            return (null, $"Enter the amount taken each paycheck for “{name}”, like 12.50.");
        }
        return (new DeductionInput(Id, Name, Kind, cents, null, start), null);
    }
}

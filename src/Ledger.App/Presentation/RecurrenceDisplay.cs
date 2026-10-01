using System.Globalization;
using Ledger.Core.Models;

namespace Ledger.App.Presentation;

/// <summary>Plain-language descriptions of recurring schedules.</summary>
public static class RecurrenceDisplay
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");

    public static IReadOnlyList<(string Label, RecurrenceFrequency Value)> Options { get; } =
    [
        ("Every week", RecurrenceFrequency.Weekly),
        ("Every two weeks", RecurrenceFrequency.Biweekly),
        ("Every month", RecurrenceFrequency.Monthly),
        ("Every three months", RecurrenceFrequency.Quarterly),
        ("Every year", RecurrenceFrequency.Yearly),
    ];

    /// <summary>"Monthly on the 15th", "Every 2 weeks on Friday", "Yearly on Mar 3".</summary>
    public static string Describe(RecurrenceFrequency frequency, DateOnly start) => frequency switch
    {
        RecurrenceFrequency.Weekly => $"Weekly on {start.ToString("dddd", Culture)}",
        RecurrenceFrequency.Biweekly => $"Every 2 weeks on {start.ToString("dddd", Culture)}",
        RecurrenceFrequency.Monthly => $"Monthly on the {DayOfMonth(start)}",
        RecurrenceFrequency.Quarterly => $"Every 3 months on the {DayOfMonth(start)}",
        _ => $"Yearly on {start.ToString("MMM d", Culture)}",
    };

    public static string ShortDate(DateOnly date) => date.ToString("ddd, MMM d", Culture);

    /// <summary>"15th", or "31st (or the last day)" when some months are shorter.</summary>
    private static string DayOfMonth(DateOnly start)
    {
        var day = start.Day;
        var suffix = (day % 100) is 11 or 12 or 13 ? "th" : (day % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        return day > 28 ? $"{day}{suffix} (or the last day)" : $"{day}{suffix}";
    }
}

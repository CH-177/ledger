using System.Globalization;
using System.Text.RegularExpressions;

namespace Ledger.Core.Payroll;

/// <summary>
/// A shift read from pasted text, for the user to confirm before saving. <see cref="ScheduledMinutes"/>
/// is the shift length as scheduled; <see cref="BreakMinutes"/> is set only when the text states a
/// break, otherwise the job's break rule applies.
/// </summary>
public sealed record ParsedShift(DateOnly Date, int ScheduledMinutes, TimeOnly? Start, TimeOnly? End, int? BreakMinutes, string Source);

/// <summary>What a paste produced. Nothing is dropped silently: unreadable text is returned.</summary>
public sealed record ScheduleParseResult(
    IReadOnlyList<ParsedShift> Shifts,
    IReadOnlyList<string> DaysOff,
    IReadOnlyList<string> Unreadable);

/// <summary>
/// Reads shifts from schedule text copied out of an employer's app or site. Understands dates
/// like "9/29", "Oct. 2nd", "Tue Sep 29", or a bare "29th" (month taken from context), and
/// either hours ("4.5hrs") or time ranges ("4:00 PM - 9:30 PM", "4p-9:30p", "22:00-06:00").
/// </summary>
public static partial class ScheduleParser
{
    public static ScheduleParseResult Parse(string text, DateOnly today)
    {
        var shifts = new List<ParsedShift>();
        var daysOff = new List<string>();
        var unreadable = new List<string>();
        DateOnly? previous = null;
        var contextMonth = new DateOnly(today.Year, today.Month, 1);

        // Many apps put the date on one line and the times on the next: a date with nothing
        // else waits here for the following line.
        (DateOnly Date, string Source)? pending = null;

        foreach (var segment in Segments(text))
        {
            var remainder = segment;
            var date = ReadDate(ref remainder, today, previous, ref contextMonth);
            var source = segment;
            if (date is null)
            {
                if (pending is { } p && (HoursPattern().IsMatch(segment) || TimeRangePattern().IsMatch(segment) || DayOffPattern().IsMatch(segment)))
                {
                    date = p.Date;
                    source = $"{p.Source} {segment}";
                    pending = null;
                }
                else
                {
                    unreadable.Add(segment);
                    continue;
                }
            }
            else if (pending is { } stale)
            {
                unreadable.Add(stale.Source);
                pending = null;
            }
            previous = date;

            if (!DayOffPattern().IsMatch(remainder) && !HoursPattern().IsMatch(remainder) && !TimeRangePattern().IsMatch(remainder))
            {
                pending = (date.Value, segment);
                continue;
            }

            if (DayOffPattern().IsMatch(remainder) && !HoursPattern().IsMatch(remainder) && !TimeRangePattern().IsMatch(remainder))
            {
                daysOff.Add(source);
                continue;
            }

            var statedBreak = ReadBreak(ref remainder);
            var range = TimeRangePattern().Match(remainder);
            var hours = HoursPattern().Match(remainder);

            if (hours.Success)
            {
                // Explicit hours are the shift's length; times, if also present, are for display.
                var minutes = ReadHours(hours);
                var (start, end) = range.Success ? ReadRange(range) : (null, null);
                shifts.Add(new ParsedShift(date.Value, minutes, start, end, statedBreak, source));
            }
            else if (range.Success && ReadRange(range) is ({ } start, { } end))
            {
                var span = (int)(end - start).TotalMinutes;   // TimeOnly subtraction wraps past midnight
                if (span == 0) span = 24 * 60;
                if (statedBreak >= span)
                {
                    unreadable.Add(source);
                    continue;
                }
                shifts.Add(new ParsedShift(date.Value, span, start, end, statedBreak, source));
            }
            else
            {
                unreadable.Add(source);
            }
        }
        if (pending is { } leftover)
        {
            unreadable.Add(leftover.Source);
        }
        return new ScheduleParseResult(shifts, daysOff, unreadable);
    }

    /// <summary>Splits on new lines, semicolons, and commas that aren't part of "Oct 2, 2026" or "Tuesday, Sept 29".</summary>
    private static IEnumerable<string> Segments(string text) =>
        text.Split('\n')
            .SelectMany(line => SegmentSplit().Split(line))
            .Select(s => s.Trim())
            .Where(s => s.Length > 0);

    private static DateOnly? ReadDate(ref string text, DateOnly today, DateOnly? previous, ref DateOnly contextMonth)
    {
        Match m;
        if ((m = NumericDatePattern().Match(text)).Success)
        {
            var month = int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture);
            var day = int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture);
            int? year = m.Groups["y"].Success ? NormalizeYear(int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture)) : null;
            return Take(ref text, m, Build(year ?? InferYear(month, today), month, day), ref contextMonth);
        }
        if ((m = MonthNamePattern().Match(text)).Success)
        {
            var month = MonthNumber(m.Groups["mon"].Value);
            var day = int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture);
            int? year = m.Groups["y"].Success ? int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture) : null;
            return Take(ref text, m, Build(year ?? InferYear(month, today), month, day), ref contextMonth);
        }
        if ((m = BareDayPattern().Match(text)).Success)
        {
            var day = int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture);
            var candidate = Build(contextMonth.Year, contextMonth.Month, day);
            // Days run forward: a smaller day than the previous one is next month; with no
            // previous date, a day well before today is assumed to be next month.
            var floor = previous ?? today.AddDays(-14);
            for (var i = 0; candidate is not null && candidate < floor && i < 2; i++)
            {
                var next = contextMonth.AddMonths(1);
                candidate = Build(next.Year, next.Month, day);
                if (candidate is not null) contextMonth = next;
            }
            return Take(ref text, m, candidate, ref contextMonth);
        }
        if ((m = WeekdayOnlyPattern().Match(text)).Success)
        {
            var target = WeekdayNumber(m.Groups["wd"].Value);
            var from = previous?.AddDays(1) ?? today;
            var date = from.AddDays(((int)target - (int)from.DayOfWeek + 7) % 7);
            return Take(ref text, m, date, ref contextMonth);
        }
        return null;
    }

    private static DateOnly? Take(ref string text, Match m, DateOnly? date, ref DateOnly contextMonth)
    {
        if (date is { } d)
        {
            contextMonth = new DateOnly(d.Year, d.Month, 1);
            text = text.Remove(m.Index, m.Length);
        }
        return date;
    }

    private static DateOnly? Build(int year, int month, int day) =>
        month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month) ? new DateOnly(year, month, day) : null;

    /// <summary>Schedules look ahead: a month far behind today is next year, far ahead is last year.</summary>
    private static int InferYear(int month, DateOnly today) =>
        month < today.Month - 6 ? today.Year + 1 : month > today.Month + 6 ? today.Year - 1 : today.Year;

    private static int NormalizeYear(int y) => y < 100 ? 2000 + y : y;

    private static int ReadHours(Match m)
    {
        if (m.Groups["hh"].Success)
        {
            return int.Parse(m.Groups["hh"].Value, CultureInfo.InvariantCulture) * 60 + int.Parse(m.Groups["mm"].Value, CultureInfo.InvariantCulture);
        }
        return (int)Math.Round(decimal.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture) * 60);
    }

    private static int? ReadBreak(ref string text)
    {
        var m = BreakPattern().Match(text);
        if (!m.Success)
        {
            return null;
        }
        text = text.Remove(m.Index, m.Length);
        return m.Groups["bh"].Success
            ? int.Parse(m.Groups["bh"].Value, CultureInfo.InvariantCulture) * 60 + int.Parse(m.Groups["bm"].Value, CultureInfo.InvariantCulture)
            : int.Parse((m.Groups["b1"].Success ? m.Groups["b1"] : m.Groups["b2"]).Value, CultureInfo.InvariantCulture);
    }

    private static (TimeOnly? Start, TimeOnly? End) ReadRange(Match m)
    {
        int sh = int.Parse(m.Groups["sh"].Value, CultureInfo.InvariantCulture), sm = m.Groups["sm"].Success ? int.Parse(m.Groups["sm"].Value, CultureInfo.InvariantCulture) : 0;
        int eh = int.Parse(m.Groups["eh"].Value, CultureInfo.InvariantCulture), em = m.Groups["em"].Success ? int.Parse(m.Groups["em"].Value, CultureInfo.InvariantCulture) : 0;
        var sAp = Meridiem(m.Groups["sap"].Value);
        var eAp = Meridiem(m.Groups["eap"].Value);

        if (sh > 12 || eh > 12 || (sh == 0 && sAp is null))
        {
            return (Time(sh, sm), Time(eh, em)); // 24-hour clock
        }
        if (sAp is null && eAp is not null)
        {
            // "10-2pm" is 10am–2pm; "4-9:30pm" is 4pm–9:30pm.
            sAp = eAp == 'p' && To24(sh, 'p') > To24(eh, 'p') ? 'a' : eAp;
        }
        else if (sAp is null && eAp is null)
        {
            // No am/pm at all: retail shifts starting 1–6 are afternoons; 7–12 are mornings.
            sAp = sh is >= 1 and <= 6 ? 'p' : 'a';
            eAp = To24(eh, sAp.Value) > To24(sh, sAp.Value) ? sAp : (sAp == 'a' ? 'p' : 'a');
        }
        eAp ??= sAp;
        return (Time(To24(sh, sAp!.Value), sm), Time(To24(eh, eAp!.Value), em));
    }

    private static char? Meridiem(string s) => string.IsNullOrEmpty(s) ? null : char.ToLowerInvariant(s[0]);
    private static int To24(int hour, char meridiem) => meridiem == 'p' ? (hour % 12) + 12 : hour % 12;
    private static TimeOnly? Time(int h, int m) => h is >= 0 and <= 23 && m is >= 0 and <= 59 ? new TimeOnly(h, m) : null;

    private static int MonthNumber(string name) =>
        Array.FindIndex(["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"],
            p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) + 1;

    private static DayOfWeek WeekdayNumber(string name) =>
        (DayOfWeek)Array.FindIndex(["sun", "mon", "tue", "wed", "thu", "fri", "sat"],
            p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"(?<!\b(?:sun|mon|tue|wed|thu|fri|sat)[a-z]*\.?)[;,](?!\s*\d{4}\b)", RegexOptions.IgnoreCase)]
    private static partial Regex SegmentSplit();

    [GeneratedRegex(@"\b(?<m>\d{1,2})/(?<d>\d{1,2})(?:/(?<y>\d{2,4}))?\b")]
    private static partial Regex NumericDatePattern();

    [GeneratedRegex(@"\b(?<mon>jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\.?\s*(?<d>\d{1,2})(?:st|nd|rd|th)?\b(?:,?\s*(?<y>\d{4})\b)?", RegexOptions.IgnoreCase)]
    private static partial Regex MonthNamePattern();

    /// <summary>A day number at the start, marked by an ordinal ("29th") or followed by ":" / "-".</summary>
    [GeneratedRegex(@"^\s*(?:(?:sun|mon|tue|wed|thu|fri|sat)[a-z]*\.?,?\s+)?(?:the\s+)?(?<d>\d{1,2})(?:(?:st|nd|rd|th)\b|(?=\s*[:\-–](?!\d)))\s*[:\-–]?", RegexOptions.IgnoreCase)]
    private static partial Regex BareDayPattern();

    [GeneratedRegex(@"^\s*(?<wd>sun|mon|tue|wed|thu|fri|sat)[a-z]*\.?\b\s*[:\-–]?", RegexOptions.IgnoreCase)]
    private static partial Regex WeekdayOnlyPattern();

    [GeneratedRegex(@"(?:(?<hh>\d{1,2}):(?<mm>\d{2})|(?<h>\d{1,2}(?:\.\d+)?))\s*(?:h|hr|hrs|hour|hours)\b", RegexOptions.IgnoreCase)]
    private static partial Regex HoursPattern();

    [GeneratedRegex(@"(?<sh>\d{1,2})(?::(?<sm>\d{2}))?\s*(?<sap>a\.?m?\.?|p\.?m?\.?)?\s*(?:-|–|—|to)\s*(?<eh>\d{1,2})(?::(?<em>\d{2}))?\s*(?<eap>a\.?m?\.?|p\.?m?\.?)?(?![\d:])", RegexOptions.IgnoreCase)]
    private static partial Regex TimeRangePattern();

    [GeneratedRegex(@"\(?\s*(?:(?<b1>\d{1,3})\s*(?:m|min|mins|minutes?)\s*(?:unpaid\s+)?(?:break|meal|lunch)|(?:break|meal|lunch)\s*:?\s*(?:(?<bh>\d):(?<bm>\d{2})|(?<b2>\d{1,3})\s*(?:m|min|mins|minutes?)?))\s*\)?", RegexOptions.IgnoreCase)]
    private static partial Regex BreakPattern();

    [GeneratedRegex(@"\b(off|day off|time off|unavailable|not scheduled|pto|vacation)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DayOffPattern();
}

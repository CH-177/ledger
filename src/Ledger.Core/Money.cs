using System.Globalization;

namespace Ledger.Core;

/// <summary>How a sign is shown when formatting an amount.</summary>
public enum MoneySign
{
    /// <summary>"-$5.00" for negatives, "$5.00" otherwise.</summary>
    NegativeOnly,

    /// <summary>"+$5.00" / "-$5.00"; zero has no sign. Used for inflow/outflow lists.</summary>
    Always,
}

/// <summary>
/// Formatting and parsing for monetary amounts. All amounts are integer cents;
/// this is the only place they are converted to and from text.
/// </summary>
public static class Money
{
    public const int CentsPerUnit = 100;

    /// <summary>
    /// The largest magnitude accepted for any single entered amount ($999,999,999.99).
    /// Far beyond personal-finance needs, and small enough that sums can never overflow.
    /// </summary>
    public const long MaxAmountCents = 99_999_999_999;

    // V1 is USD-only. Currency selection belongs here when it arrives.
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");
    private const string Symbol = "$";

    /// <summary>Formats cents as currency, e.g. 3790 → "$37.90".</summary>
    public static string Format(long cents, MoneySign sign = MoneySign.NegativeOnly)
    {
        // decimal avoids overflow when negating long.MinValue.
        var magnitude = Math.Abs((decimal)cents) / CentsPerUnit;
        var body = Symbol + magnitude.ToString("#,##0.00", Culture);
        return cents switch
        {
            < 0 => "-" + body,
            > 0 when sign == MoneySign.Always => "+" + body,
            _ => body,
        };
    }

    /// <summary>Formats cents without the fractional part when it is zero, e.g. 120000 → "$1,200".</summary>
    public static string FormatCompact(long cents)
    {
        return cents % CentsPerUnit == 0
            ? (cents < 0 ? "-" : "") + Symbol + (Math.Abs((decimal)cents) / CentsPerUnit).ToString("#,##0", Culture)
            : Format(cents);
    }

    /// <summary>Formats cents for an editable text field, without symbol or grouping: 124000 → "1240.00".</summary>
    public static string FormatForEditing(long cents) =>
        ((decimal)cents / CentsPerUnit).ToString("0.00", Culture);

    /// <summary>
    /// Parses user input such as "37.9", "$1,234.56", "-5" into cents.
    /// Rejects more than two decimal places rather than silently rounding.
    /// </summary>
    public static bool TryParse(string? text, out long cents)
    {
        cents = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var cleaned = text.Trim().Replace(Symbol, "", StringComparison.Ordinal);
        const NumberStyles styles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint
            | NumberStyles.AllowThousands | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite;
        if (!decimal.TryParse(cleaned, styles, Culture, out var value))
        {
            return false;
        }

        var scaled = value * CentsPerUnit;
        if (scaled != decimal.Truncate(scaled) || scaled > long.MaxValue || scaled < long.MinValue)
        {
            return false;
        }

        cents = (long)scaled;
        return true;
    }

    /// <summary>Rounds a fractional number of cents to whole cents (half away from zero, as payroll does).</summary>
    public static long RoundToCents(decimal cents) => (long)Math.Round(cents, MidpointRounding.AwayFromZero);

    /// <summary>Converts cents to a decimal unit amount, for rate/percentage math only.</summary>
    public static decimal ToDecimal(long cents) => (decimal)cents / CentsPerUnit;

    /// <summary>Sums amounts, throwing on overflow instead of wrapping.</summary>
    public static long Sum(IEnumerable<long> amounts)
    {
        long total = 0;
        foreach (var a in amounts)
        {
            total = checked(total + a);
        }
        return total;
    }
}

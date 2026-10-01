namespace Ledger.Core.Services;

/// <summary>
/// The app's colour schemes. Independent of the System / Light / Dark theme: every scheme has a
/// light and a dark variant, and Windows high contrast overrides any scheme.
/// </summary>
public enum ColorScheme
{
    /// <summary>The default: warm off-white, charcoal, dusty blue and sage.</summary>
    Ledger,

    /// <summary>Frost blue with dusty white.</summary>
    Frost,

    /// <summary>Forest green and diorite, with berry as the accent.</summary>
    Forest,

    /// <summary>Terracotta, redwood and gold.</summary>
    Terracotta,
}

public static class ColorSchemes
{
    public static IReadOnlyList<ColorScheme> All { get; } = Enum.GetValues<ColorScheme>();

    /// <summary>
    /// Reads a stored <see cref="SettingKeys.ColorScheme"/> value. Anything missing or unknown
    /// (including numbers) means the default <see cref="ColorScheme.Ledger"/> scheme.
    /// </summary>
    public static ColorScheme Parse(string? stored)
    {
        var name = stored?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return ColorScheme.Ledger;
        }
        foreach (var scheme in All)
        {
            if (string.Equals(scheme.ToString(), name, StringComparison.OrdinalIgnoreCase))
            {
                return scheme;
            }
        }
        return ColorScheme.Ledger;
    }

    /// <summary>The value stored in the settings table for <paramref name="scheme"/>.</summary>
    public static string ToStoredValue(ColorScheme scheme) =>
        Enum.IsDefined(scheme) ? scheme.ToString() : ColorScheme.Ledger.ToString();
}

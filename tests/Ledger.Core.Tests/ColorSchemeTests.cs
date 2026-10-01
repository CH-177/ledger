using System.Globalization;
using System.Xml.Linq;
using Ledger.Core.Services;

namespace Ledger.Core.Tests;

/// <summary>
/// The colour scheme setting, and the palettes themselves: every scheme's XAML file
/// (src/Ledger.App/Resources/Themes/Schemes) is read here and checked for complete keys,
/// an unchanged high-contrast dictionary, WCAG contrast and clearly different semantic colours.
/// </summary>
public class ColorSchemeTests
{
    [Theory]
    [InlineData(null, ColorScheme.Ledger)]
    [InlineData("", ColorScheme.Ledger)]
    [InlineData("  ", ColorScheme.Ledger)]
    [InlineData("Ledger", ColorScheme.Ledger)]
    [InlineData("Frost", ColorScheme.Frost)]
    [InlineData("forest", ColorScheme.Forest)]
    [InlineData(" TERRACOTTA ", ColorScheme.Terracotta)]
    [InlineData("Ocean", ColorScheme.Ledger)]
    [InlineData("2", ColorScheme.Ledger)]
    [InlineData("99", ColorScheme.Ledger)]
    public void Missing_or_unknown_values_mean_the_default_scheme(string? stored, ColorScheme expected) =>
        Assert.Equal(expected, ColorSchemes.Parse(stored));

    [Fact]
    public void Every_scheme_round_trips_through_its_stored_value()
    {
        foreach (var scheme in ColorSchemes.All)
        {
            Assert.Equal(scheme, ColorSchemes.Parse(ColorSchemes.ToStoredValue(scheme)));
        }
        Assert.Equal("Ledger", ColorSchemes.ToStoredValue((ColorScheme)42));
    }

    [Fact]
    public async Task Scheme_choice_persists_in_the_settings_table()
    {
        await using var db = await TestDatabase.CreateAsync();
        var settings = new SettingsService(db.Factory);

        Assert.Equal(ColorScheme.Ledger, ColorSchemes.Parse(await settings.GetAsync(SettingKeys.ColorScheme)));

        await settings.SetAsync(SettingKeys.ColorScheme, ColorSchemes.ToStoredValue(ColorScheme.Forest));
        Assert.Equal(ColorScheme.Forest, ColorSchemes.Parse(await new SettingsService(db.Factory).GetAsync(SettingKeys.ColorScheme)));

        // Independent of the theme setting.
        await settings.SetAsync(SettingKeys.Theme, "Dark");
        Assert.Equal(ColorScheme.Forest, ColorSchemes.Parse(await settings.GetAsync(SettingKeys.ColorScheme)));
    }

    // ---- The palettes ---------------------------------------------------------------------------

    public static TheoryData<ColorScheme, string> SchemesAndThemes()
    {
        var data = new TheoryData<ColorScheme, string>();
        foreach (var scheme in ColorSchemes.All)
        {
            data.Add(scheme, "Light");
            data.Add(scheme, "Dark");
        }
        return data;
    }

    [Fact]
    public void Every_scheme_defines_the_same_keys_as_the_default_and_keeps_high_contrast_unchanged()
    {
        var reference = Load(ColorScheme.Ledger);
        var referenceKeys = Keys(Theme(reference, "Light"));
        Assert.Equal(referenceKeys, Keys(Theme(reference, "Dark")));
        var referenceHighContrast = Theme(reference, "HighContrast").ToString(SaveOptions.DisableFormatting);

        foreach (var scheme in ColorSchemes.All)
        {
            var doc = Load(scheme);
            Assert.Equal(referenceKeys, Keys(Theme(doc, "Light")));
            Assert.Equal(referenceKeys, Keys(Theme(doc, "Dark")));
            Assert.Equal(referenceHighContrast, Theme(doc, "HighContrast").ToString(SaveOptions.DisableFormatting));
        }
    }

    [Theory]
    [MemberData(nameof(SchemesAndThemes))]
    public void Text_meets_WCAG_contrast_on_backgrounds_cards_and_tags(ColorScheme scheme, string theme)
    {
        var c = Colors(scheme, theme);
        foreach (var bg in new[] { "Background", "Surface" })
        {
            foreach (var fg in new[] { "TextPrimary", "TextSecondary", "TextTertiary", "Positive", "Negative", "Warning", "Accent" })
            {
                AssertContrast(c, fg, bg, 4.5);
            }
        }
        foreach (var tag in new[] { "SageSoft", "LavenderSoft", "PeachSoft", "BlueSoft" })
        {
            AssertContrast(c, "TextPrimary", tag, 4.5);
        }
        AssertContrast(c, "TextPrimary", "SurfaceAlt", 4.5);
        AssertContrast(c, "TextSecondary", "SurfaceAlt", 4.5);
        AssertContrast(c, "Accent", "BlueSoft", 3); // icons in their circles
    }

    [Theory]
    [MemberData(nameof(SchemesAndThemes))]
    public void Accent_fills_stand_out_and_carry_readable_text(ColorScheme scheme, string theme)
    {
        var c = Colors(scheme, theme);
        // WinUI fills accent controls with SystemAccentColorDark1 (light) / Light2 (dark), with white / black
        // text on top, and draws accent text (links) in Dark2 / Light3.
        var fill = theme == "Light" ? "SystemAccentColorDark1" : "SystemAccentColorLight2";
        var text = theme == "Light" ? "SystemAccentColorDark2" : "SystemAccentColorLight3";
        c["OnAccent"] = theme == "Light" ? "#FFFFFF" : "#000000";

        AssertContrast(c, fill, "Background", 3);
        AssertContrast(c, fill, "Surface", 3);
        AssertContrast(c, fill, "Track", 3);
        AssertContrast(c, "OnAccent", fill, 4.5);
        AssertContrast(c, text, "Background", 4.5);
        AssertContrast(c, text, "Surface", 4.5);
    }

    [Theory]
    [MemberData(nameof(SchemesAndThemes))]
    public void Semantic_colours_and_the_actual_and_forecast_tags_stay_distinct(ColorScheme scheme, string theme)
    {
        var c = Colors(scheme, theme);

        // Positive stays green, negative red, warning amber, whatever the scheme.
        Assert.InRange(Hue(c["Positive"]), 130, 175);
        Assert.True(Hue(c["Negative"]) is < 45 or > 350, $"Negative hue {Hue(c["Negative"]):0}");
        Assert.InRange(Hue(c["Warning"]), 65, 95);

        foreach (var (a, b) in new[] { ("Positive", "Negative"), ("Positive", "Warning"), ("Negative", "Warning"),
                                       ("Accent", "Negative"), ("Accent", "Positive"), ("Accent", "Warning") })
        {
            Assert.True(DeltaE(c[a], c[b]) >= 24, $"{scheme} {theme}: {a} and {b} are too alike (ΔE {DeltaE(c[a], c[b]):0.0})");
        }

        // "Actual" (sage) and "Forecast" (lavender) must never look alike.
        Assert.True(DeltaE(c["SageSoft"], c["LavenderSoft"]) >= 12,
            $"{scheme} {theme}: Actual and Forecast tags are too alike (ΔE {DeltaE(c["SageSoft"], c["LavenderSoft"]):0.0})");
    }

    // ---- Helpers --------------------------------------------------------------------------------

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument Load(ColorScheme scheme)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return XDocument.Load(Path.Combine(dir.FullName, "src", "Ledger.App", "Resources", "Themes", "Schemes", scheme + ".xaml"));
    }

    private static XElement Theme(XDocument doc, string theme) =>
        doc.Descendants().Single(e => e.Name.LocalName == "ResourceDictionary" && (string?)e.Attribute(X + "Key") == theme);

    private static List<string> Keys(XElement theme) =>
        theme.Elements().Select(e => (string)e.Attribute(X + "Key")!).Order(StringComparer.Ordinal).ToList();

    /// <summary>"LedgerSurfaceBrush" is returned as "Surface"; Color resources keep their names.</summary>
    private static Dictionary<string, string> Colors(ColorScheme scheme, string theme) =>
        Theme(Load(scheme), theme).Elements().ToDictionary(
            e => ((string)e.Attribute(X + "Key")!).Replace("Ledger", "").Replace("Brush", ""),
            e => e.Name.LocalName == "Color" ? e.Value.Trim() : (string)e.Attribute("Color")!);

    private static void AssertContrast(Dictionary<string, string> c, string fg, string bg, double minimum)
    {
        var ratio = Contrast(c[fg], c[bg]);
        Assert.True(ratio >= minimum, $"{fg} {c[fg]} on {bg} {c[bg]}: {ratio:0.00}:1, needs {minimum}:1");
    }

    private static (double R, double G, double B) Linear(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 8)
        {
            hex = hex[2..];
        }
        static double Channel(string h) =>
            int.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0 is var v && v <= 0.04045
                ? v / 12.92
                : Math.Pow((v + 0.055) / 1.055, 2.4);
        return (Channel(hex[..2]), Channel(hex[2..4]), Channel(hex[4..6]));
    }

    private static double Luminance(string hex)
    {
        var (r, g, b) = Linear(hex);
        return 0.2126 * r + 0.7152 * g + 0.0722 * b;
    }

    /// <summary>WCAG 2 contrast ratio.</summary>
    private static double Contrast(string a, string b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static (double L, double A, double B) Lab(string hex)
    {
        var (r, g, b) = Linear(hex);
        double x = (0.4124 * r + 0.3576 * g + 0.1805 * b) / 0.95047;
        double y = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        double z = (0.0193 * r + 0.1192 * g + 0.9505 * b) / 1.08883;
        static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : 7.787 * t + 16.0 / 116;
        return (116 * F(y) - 16, 500 * (F(x) - F(y)), 200 * (F(y) - F(z)));
    }

    /// <summary>CIE76 colour difference; above ~20 two colours read as clearly different.</summary>
    private static double DeltaE(string a, string b)
    {
        var p = Lab(a);
        var q = Lab(b);
        return Math.Sqrt(Math.Pow(p.L - q.L, 2) + Math.Pow(p.A - q.A, 2) + Math.Pow(p.B - q.B, 2));
    }

    /// <summary>CIELAB hue angle in degrees.</summary>
    private static double Hue(string hex)
    {
        var lab = Lab(hex);
        var h = Math.Atan2(lab.B, lab.A) * 180 / Math.PI;
        return h < 0 ? h + 360 : h;
    }
}

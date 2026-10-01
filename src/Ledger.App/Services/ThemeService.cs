using Ledger.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Ledger.App.Services;

/// <summary>
/// Applies and persists the System / Light / Dark preference and the colour scheme.
/// The two are independent: every scheme has a light and a dark variant, and Windows high contrast
/// overrides any scheme (each scheme file carries the same HighContrast dictionary).
/// </summary>
public sealed class ThemeService(SettingsService settings)
{
    private const string SchemeFolder = "/Resources/Themes/Schemes/";

    private FrameworkElement? _root;

    public ElementTheme Current { get; private set; } = ElementTheme.Default;

    public ColorScheme CurrentScheme { get; private set; } = ColorScheme.Ledger;

    /// <summary>Raised after the theme or scheme changes so the window can restyle its caption buttons.</summary>
    public event EventHandler? ThemeChanged;

    /// <summary>Whether the decorative art beside pages is shown (on wide enough windows).</summary>
    public bool ShowSideArt { get; private set; } = true;

    /// <summary>Raised when <see cref="ShowSideArt"/> changes.</summary>
    public event EventHandler? SideArtChanged;

    public async Task SetShowSideArtAsync(bool show)
    {
        ShowSideArt = show;
        SideArtChanged?.Invoke(this, EventArgs.Empty);
        await settings.SetAsync(SettingKeys.SideArt, show ? "true" : "false");
    }

    public async Task InitializeAsync(FrameworkElement root)
    {
        _root = root;
        var storedScheme = await settings.GetAsync(SettingKeys.ColorScheme);
        var storedTheme = await settings.GetAsync(SettingKeys.Theme);
        ShowSideArt = await settings.GetAsync(SettingKeys.SideArt) != "false";
        SideArtChanged?.Invoke(this, EventArgs.Empty);
        var swapped = LoadScheme(ColorSchemes.Parse(storedScheme));
        Apply(Enum.TryParse<ElementTheme>(storedTheme, out var theme) ? theme : ElementTheme.Default, refresh: swapped);
    }

    public async Task SetThemeAsync(ElementTheme theme)
    {
        Apply(theme, refresh: false);
        await settings.SetAsync(SettingKeys.Theme, theme.ToString());
    }

    public async Task SetSchemeAsync(ColorScheme scheme)
    {
        if (LoadScheme(scheme))
        {
            Apply(Current, refresh: true);
        }
        await settings.SetAsync(SettingKeys.ColorScheme, ColorSchemes.ToStoredValue(scheme));
    }

    /// <summary>
    /// A colour from the active scheme for the given light/dark theme, for the few places that
    /// can't use {ThemeResource} (the window's caption buttons). Brushes return their colour.
    /// </summary>
    public Color GetColor(string key, bool dark)
    {
        var dictionary = FindSchemeDictionary(out _);
        if (dictionary?.ThemeDictionaries.TryGetValue(dark ? "Dark" : "Light", out var themed) == true
            && themed is ResourceDictionary theme && theme.TryGetValue(key, out var value))
        {
            return value switch
            {
                SolidColorBrush brush => brush.Color,
                Color color => color,
                _ => throw new InvalidOperationException($"{key} is not a colour."),
            };
        }
        throw new KeyNotFoundException($"{key} is not in the colour scheme.");
    }

    /// <summary>Swaps the scheme dictionary in App.Resources for <paramref name="scheme"/>'s; false if it was already active.</summary>
    private bool LoadScheme(ColorScheme scheme)
    {
        CurrentScheme = scheme;
        var existing = FindSchemeDictionary(out var index);
        if (existing?.Source.AbsolutePath.EndsWith($"/{scheme}.xaml", StringComparison.OrdinalIgnoreCase) == true)
        {
            return false;
        }

        var replacement = new ResourceDictionary { Source = new Uri($"ms-appx://{SchemeFolder}{scheme}.xaml") };
        var merged = Application.Current.Resources.MergedDictionaries;
        if (index >= 0)
        {
            merged[index] = replacement;
        }
        else
        {
            // Right after XamlControlsResources, as in App.xaml, so later dictionaries can use its brushes.
            merged.Insert(Math.Min(1, merged.Count), replacement);
        }
        return true;
    }

    private static ResourceDictionary? FindSchemeDictionary(out int index)
    {
        var merged = Application.Current.Resources.MergedDictionaries;
        for (index = 0; index < merged.Count; index++)
        {
            if (merged[index].Source?.AbsolutePath.Contains(SchemeFolder, StringComparison.OrdinalIgnoreCase) == true)
            {
                return merged[index];
            }
        }
        index = -1;
        return null;
    }

    private void Apply(ElementTheme theme, bool refresh)
    {
        Current = theme;
        if (_root is not null)
        {
            if (refresh)
            {
                // {ThemeResource} references only re-resolve when the element's theme changes, so after
                // swapping the scheme dictionary flip to the opposite theme and back. Everything under the
                // root (pages, cards, InfoBars, the navigation pane) then picks up the new brushes.
                var opposite = _root.ActualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
                _root.RequestedTheme = opposite;
            }
            _root.RequestedTheme = theme;
        }
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }
}

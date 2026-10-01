using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ledger.App.Infrastructure;
using Ledger.App.Services;
using Ledger.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

namespace Ledger.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private static readonly ElementTheme[] ThemeOptions = [ElementTheme.Default, ElementTheme.Light, ElementTheme.Dark];

    private readonly ThemeService _themes;
    private readonly NavigationService _navigation;
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly Ledger.Core.Services.IncomeService _income;
    private readonly Ledger.Core.Services.SuggestionService _suggestions;
    private readonly WelcomeTour _tour;
    private bool _loadingSuggestions;

    public SettingsViewModel(ThemeService themes, NavigationService navigation, Ledger.Core.Services.IncomeService income,
        Ledger.Core.Services.SuggestionService suggestions, WelcomeTour tour, ILogger<SettingsViewModel> logger)
    {
        _tour = tour;
        _themes = themes;
        _navigation = navigation;
        _income = income;
        _suggestions = suggestions;
        _logger = logger;
        ThemeIndex = Math.Max(0, Array.IndexOf(ThemeOptions, themes.Current));
        SchemeIndex = Math.Max(0, IndexOf(ColorSchemes.All, themes.CurrentScheme));
        ShowSideArt = themes.ShowSideArt;
    }

    /// <summary>The squiggly art in the empty space beside pages.</summary>
    [ObservableProperty]
    public partial bool ShowSideArt { get; set; }

    partial void OnShowSideArtChanged(bool value)
    {
        if (value != _themes.ShowSideArt)
        {
            _ = _themes.SetShowSideArtAsync(value);
        }
    }

    /// <summary>0 = System, 1 = Light, 2 = Dark; bound to the theme radio buttons.</summary>
    [ObservableProperty]
    public partial int ThemeIndex { get; set; }

    /// <summary>The colour scheme, as an index into <see cref="ColorSchemes.All"/> (Ledger, Frost, Forest, Terracotta).</summary>
    [ObservableProperty]
    public partial int SchemeIndex { get; set; }

    public string DataLocation => AppPaths.DataDirectory;

    public string VersionText =>
        "Version " + (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");

    [RelayCommand]
    private void ManageCategories() => _navigation.Navigate(AppPage.Categories);

    [RelayCommand]
    private void StartTour() => _tour.Start();

    [RelayCommand]
    private void OpenHelp() => _navigation.Navigate(AppPage.Help, "settings");

    // ---- Home local tax (used for paycheck estimates) ------------------------------------------

    [ObservableProperty] public partial string HomeLocation { get; set; } = "";
    [ObservableProperty] public partial string HomePsd { get; set; } = "";
    [ObservableProperty] public partial string HomeEitText { get; set; } = "";
    [ObservableProperty] public partial string? HomeMessage { get; set; }

    /// <summary>Off: paychecks are estimated with federal tax and FICA only.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutsidePennsylvania))]
    public partial bool InPennsylvania { get; set; } = true;

    public bool OutsidePennsylvania => !InPennsylvania;

    /// <summary>Spot entries that repeat on a schedule and suggest them as bills (on the Bills page).</summary>
    [ObservableProperty] public partial bool SpotRepeats { get; set; } = true;

    partial void OnSpotRepeatsChanged(bool value)
    {
        if (!_loadingSuggestions)
        {
            _ = _suggestions.SetSpottingRepeatsAsync(value);
        }
    }

    public async Task LoadAsync()
    {
        _loadingSuggestions = true;
        try
        {
            SpotRepeats = await _suggestions.IsSpottingRepeatsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load suggestion settings");
        }
        finally
        {
            _loadingSuggestions = false;
        }
        try
        {
            var home = await _income.GetHomeTaxSettingsAsync();
            HomeLocation = home.LocationName;
            HomePsd = home.PsdCode ?? "";
            HomeEitText = (home.ResidentEitBasisPoints / 100m).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            InPennsylvania = home.InPennsylvania;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load home tax settings");
        }
    }

    [RelayCommand]
    private async Task SaveHomeAsync()
    {
        // Outside Pennsylvania the local fields are hidden; whatever they held is kept for switching back.
        var parsed = decimal.TryParse(HomeEitText.Trim().TrimEnd('%'), System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out var pct);
        if (!parsed && !InPennsylvania)
        {
            pct = 0;
        }
        else if (!parsed)
        {
            HomeMessage = "Enter the rate as a percent, like 1.5.";
            return;
        }
        try
        {
            await _income.SetHomeTaxSettingsAsync(new Ledger.Core.Services.HomeTaxSettings(
                string.IsNullOrWhiteSpace(HomeLocation) ? "Home" : HomeLocation, HomePsd, (int)Math.Round(pct * 100), InPennsylvania));
            HomeMessage = InPennsylvania ? "Saved. Paycheck estimates now use this rate." : "Saved. Paycheck estimates now leave out state and local taxes.";
        }
        catch (Ledger.Core.LedgerValidationException ex)
        {
            HomeMessage = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save home tax settings");
            HomeMessage = "Something went wrong while saving. Your existing settings have not been changed.";
        }
    }

    partial void OnThemeIndexChanged(int value)
    {
        if (value < 0 || value >= ThemeOptions.Length)
        {
            return;
        }
        _ = ApplyThemeAsync(ThemeOptions[value]);
    }

    partial void OnSchemeIndexChanged(int value)
    {
        if (value < 0 || value >= ColorSchemes.All.Count)
        {
            return;
        }
        _ = ApplySchemeAsync(ColorSchemes.All[value]);
    }

    private async Task ApplySchemeAsync(ColorScheme scheme)
    {
        try
        {
            await _themes.SetSchemeAsync(scheme);
        }
        catch (Exception ex)
        {
            // The scheme still changes for this session; only persisting it failed.
            _logger.LogError(ex, "Failed to save colour scheme preference");
        }
    }

    private static int IndexOf(IReadOnlyList<ColorScheme> list, ColorScheme scheme)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] == scheme)
            {
                return i;
            }
        }
        return -1;
    }

    private async Task ApplyThemeAsync(ElementTheme theme)
    {
        try
        {
            await _themes.SetThemeAsync(theme);
        }
        catch (Exception ex)
        {
            // The theme still changes for this session; only persisting it failed.
            _logger.LogError(ex, "Failed to save theme preference");
        }
    }
}

using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ledger.App.Infrastructure;
using Ledger.App.Services;
using Ledger.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;

namespace Ledger.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private static readonly ElementTheme[] ThemeOptions = [ElementTheme.Default, ElementTheme.Light, ElementTheme.Dark];

    private readonly ThemeService _themes;
    private readonly NavigationService _navigation;
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly IncomeService _income;
    private readonly SuggestionService _suggestions;
    private readonly WelcomeTour _tour;
    private readonly GrokbotTokenStore _tokens;
    private readonly GrokbotApiHost _api;
    private bool _loadingSuggestions;
    private bool _loadingGrokbot;

    public SettingsViewModel(ThemeService themes, NavigationService navigation, IncomeService income,
        SuggestionService suggestions, WelcomeTour tour, GrokbotTokenStore tokens, GrokbotApiHost api,
        ILogger<SettingsViewModel> logger)
    {
        _tour = tour;
        _tokens = tokens;
        _api = api;
        _themes = themes;
        _navigation = navigation;
        _income = income;
        _suggestions = suggestions;
        _logger = logger;
        ThemeIndex = Math.Max(0, Array.IndexOf(ThemeOptions, themes.Current));
        SchemeIndex = Math.Max(0, IndexOf(ColorSchemes.All, themes.CurrentScheme));
        ShowSideArt = themes.ShowSideArt;
        _api.StatusChanged += (_, _) => UpdateGrokbotStatus();
        UpdateGrokbotStatus();
    }

    [ObservableProperty]
    public partial bool ShowSideArt { get; set; }

    partial void OnShowSideArtChanged(bool value)
    {
        if (value != _themes.ShowSideArt)
        {
            _ = _themes.SetShowSideArtAsync(value);
        }
    }

    [ObservableProperty]
    public partial int ThemeIndex { get; set; }

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

    // ---- Grokbot ---------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GrokbotListening))]
    public partial bool GrokbotEnabled { get; set; }

    [ObservableProperty] public partial string GrokbotPortText { get; set; } = GrokbotApiHost.DefaultPort.ToString();
    [ObservableProperty] public partial string GrokbotStatus { get; private set; } = "";
    [ObservableProperty] public partial string? GrokbotMessage { get; private set; }
    [ObservableProperty] public partial string? GrokbotTokenPreview { get; private set; }

    public bool GrokbotListening => _api.IsListening;

    private void UpdateGrokbotStatus()
    {
        GrokbotStatus = _api.IsListening
            ? $"Listening on {_api.BaseUrl}"
            : GrokbotEnabled ? "Off: couldn't start the listener"
            : "Off";
        GrokbotTokenPreview = _tokens.HasToken
            ? (_tokens.Load() is { Length: >= 8 } t ? $"{t[..4]}…{t[^4..]}" : "Saved")
            : "No token yet";
    }

    partial void OnGrokbotEnabledChanged(bool value)
    {
        if (_loadingGrokbot)
        {
            return;
        }
        _ = SetGrokbotEnabledAsync(value);
    }

    private async Task SetGrokbotEnabledAsync(bool enabled)
    {
        try
        {
            if (enabled)
            {
                if (!int.TryParse(GrokbotPortText.Trim(), out var port) || port is < 1024 or > 65535)
                {
                    GrokbotMessage = "Port must be between 1024 and 65535.";
                    _loadingGrokbot = true;
                    GrokbotEnabled = false;
                    _loadingGrokbot = false;
                    return;
                }
                await _api.StartAsync(port);
                GrokbotMessage = "On. Copy the MCP config into Cursor (or call the HTTP API with the bearer token). Writes still need Approve in Ledger.";
            }
            else
            {
                await _api.StopAsync();
                GrokbotMessage = "Off. Nothing is listening.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to toggle Grokbot API");
            GrokbotMessage = ex.Message;
            _loadingGrokbot = true;
            GrokbotEnabled = false;
            _loadingGrokbot = false;
        }
        UpdateGrokbotStatus();
    }

    [RelayCommand]
    private void GenerateToken()
    {
        var token = _tokens.GenerateAndSave();
        CopyText(token);
        GrokbotMessage = "New token generated and copied. Update LEDGER_API_TOKEN in your MCP config.";
        UpdateGrokbotStatus();
    }

    [RelayCommand]
    private void CopyToken()
    {
        if (_tokens.Load() is not { } token)
        {
            GrokbotMessage = "Generate a token first.";
            return;
        }
        CopyText(token);
        GrokbotMessage = "Token copied.";
    }

    [RelayCommand]
    private void CopyMcpConfig()
    {
        if (_tokens.Load() is not { } token)
        {
            token = _tokens.GenerateAndSave();
        }
        var mcpPath = Path.Combine(AppContext.BaseDirectory, "Ledger.Mcp.exe");
        if (!File.Exists(mcpPath))
        {
            // Dev / sibling publish layout
            var sibling = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Ledger.Mcp", "bin", "Debug", "net10.0", "Ledger.Mcp.exe"));
            if (File.Exists(sibling))
            {
                mcpPath = sibling;
            }
        }
        var port = _api.IsListening ? _api.Port : (int.TryParse(GrokbotPortText, out var p) ? p : GrokbotApiHost.DefaultPort);
        var json = $$"""
            {
              "mcpServers": {
                "ledger": {
                  "command": {{JsonEscape(mcpPath)}},
                  "env": {
                    "LEDGER_API_URL": "http://127.0.0.1:{{port}}/",
                    "LEDGER_API_TOKEN": {{JsonEscape(token)}}
                  }
                }
              }
            }
            """;
        CopyText(json);
        GrokbotMessage = "MCP config copied. Paste it into Cursor's MCP settings. Ledger must be running with Grokbot access on.";
        UpdateGrokbotStatus();
    }

    private static string JsonEscape(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static void CopyText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    // ---- Home local tax --------------------------------------------------------------------

    [ObservableProperty] public partial string HomeLocation { get; set; } = "";
    [ObservableProperty] public partial string HomePsd { get; set; } = "";
    [ObservableProperty] public partial string HomeEitText { get; set; } = "";
    [ObservableProperty] public partial string? HomeMessage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutsidePennsylvania))]
    public partial bool InPennsylvania { get; set; } = true;

    public bool OutsidePennsylvania => !InPennsylvania;

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

        _loadingGrokbot = true;
        try
        {
            GrokbotEnabled = _api.IsListening;
            GrokbotPortText = (_api.IsListening ? _api.Port : GrokbotApiHost.DefaultPort).ToString();
            UpdateGrokbotStatus();
        }
        finally
        {
            _loadingGrokbot = false;
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
            await _income.SetHomeTaxSettingsAsync(new HomeTaxSettings(
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
            _logger.LogError(ex, "Failed to save theme preference");
        }
    }
}

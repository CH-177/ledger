using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ledger.Core.Data;
using Ledger.Core.Services;
using Microsoft.Extensions.Logging;

namespace Ledger.App.ViewModels;

/// <summary>
/// Startup state for the main window: opening the database, then deciding
/// between first-launch setup and the main app.
/// </summary>
public sealed partial class ShellViewModel(
    DatabaseInitializer initializer,
    AccountService accounts,
    Services.AutoRecordService autoRecord,
    BackupService backups,
    SettingsService settings,
    ILogger<ShellViewModel> logger) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    public partial bool IsLoading { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    public partial bool HasFailed { get; set; }

    /// <summary>True until the user has created their first account.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    public partial bool IsOnboarding { get; set; }

    /// <summary>The main navigation is usable.</summary>
    public bool IsReady => !IsLoading && !HasFailed && !IsOnboarding;

    /// <summary>The daily automatic backup, off the UI thread. A failure is logged and never blocks the app.</summary>
    private async Task AutoBackUpAsync()
    {
        try
        {
            if (await settings.GetAsync(SettingKeys.AutomaticBackups) != "0")
            {
                await Task.Run(() => backups.AutoBackUpAsync());
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Automatic backup failed");
        }
    }

    /// <summary>Opens and migrates the database. Returns true when the app can continue.</summary>
    [RelayCommand]
    public async Task<bool> InitializeAsync()
    {
        IsLoading = true;
        HasFailed = false;
        try
        {
            await initializer.InitializeAsync();
            IsOnboarding = !await accounts.AnyAccountsAsync();
            if (!IsOnboarding)
            {
                await autoRecord.RunAsync();
            }
            _ = AutoBackUpAsync();
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Database initialization failed");
            HasFailed = true;
            return false;
        }
        finally
        {
            IsLoading = false;
        }
    }
}

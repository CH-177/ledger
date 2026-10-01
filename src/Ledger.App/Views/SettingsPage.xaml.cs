using Ledger.App.Services;
using Ledger.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Core;

namespace Ledger.App.Views;

public sealed partial class SettingsPage : Page
{
    private readonly DialogService _dialogs = App.Services.GetRequiredService<DialogService>();

    public SettingsViewModel ViewModel { get; } = App.Services.GetRequiredService<SettingsViewModel>();
    public DataViewModel Data { get; } = App.Services.GetRequiredService<DataViewModel>();

    public SettingsPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        await ViewModel.LoadAsync();
        await Data.LoadAsync();
    }

    public bool IsIdle(bool busy) => !busy;
    public InfoBarSeverity SeverityFor(bool isError) => isError ? InfoBarSeverity.Error : InfoBarSeverity.Success;

    private void OnMessageClosed(InfoBar sender, InfoBarClosedEventArgs args) => Data.Message = null;
    private void OnOpenDataFolderClick(object sender, RoutedEventArgs e) => Data.OpenDataFolder();
    private void OnOpenBackupsFolderClick(object sender, RoutedEventArgs e) => Data.OpenBackupsFolder();
    private async void OnBackUpClick(object sender, RoutedEventArgs e) => await Data.BackUpNowAsync();
    private async void OnExportCsvClick(object sender, RoutedEventArgs e) => await Data.ExportCsvAsync();
    private async void OnExportJsonClick(object sender, RoutedEventArgs e) => await Data.ExportJsonAsync();

    /// <summary>
    /// Pick → check → show what's inside and confirm → restore (current data saved first) → restart,
    /// so no page keeps showing the old data.
    /// </summary>
    private async void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        if (await Data.PickBackupAsync() is not { } summary)
        {
            return;
        }

        var newest = summary.NewestTransaction is { } d ? $", newest from {d:MMM d, yyyy}" : "";
        var upgrade = summary.NeedsUpgrade ? " It was made by an earlier version and will be upgraded." : "";
        var confirmed = await _dialogs.ConfirmAsync(
            "Replace your data with this backup?",
            $"The backup ({summary.FileTime:MMM d, yyyy h:mm tt}) has {summary.AccountCount} account(s) and {summary.TransactionCount} transaction(s){newest}.{upgrade}\n\n" +
            $"Everything currently in Ledger is replaced. A copy of it is saved first in {Data.BackupsFolder}, so this can be undone. Ledger restarts afterwards.",
            "Restore");
        if (!confirmed || !await Data.RestoreAsync(summary))
        {
            return;
        }

        if (AppInstance.Restart("") is var reason && reason != AppRestartFailureReason.RestartPending)
        {
            await _dialogs.ConfirmAsync("Backup restored", "Close Ledger and open it again to see the restored data.", "OK", closeText: "");
        }
    }
}

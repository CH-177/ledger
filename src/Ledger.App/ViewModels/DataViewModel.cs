using System.Diagnostics;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using Ledger.App.Infrastructure;
using Ledger.App.Services;
using Ledger.Core;
using Ledger.Core.Services;
using Microsoft.Extensions.Logging;

namespace Ledger.App.ViewModels;

/// <summary>Settings → Data: backups, restore and exports.</summary>
public sealed partial class DataViewModel(
    BackupService backups,
    ExportService exports,
    SettingsService settings,
    FilePickerService pickers,
    ILogger<DataViewModel> logger) : ObservableObject
{
    public string DataLocation => AppPaths.DataDirectory;
    public string BackupsFolder => backups.BackupsFolder;

    [ObservableProperty] public partial bool IsAutomaticOn { get; set; } = true;
    [ObservableProperty] public partial string LastAutomaticText { get; set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; set; }

    /// <summary>The result of the last action, in words.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; set; }

    [ObservableProperty] public partial bool IsError { get; set; }

    public bool HasMessage => Message is not null;
    private bool _loading;

    public async Task LoadAsync()
    {
        _loading = true;
        try
        {
            IsAutomaticOn = await settings.GetAsync(SettingKeys.AutomaticBackups) != "0";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load backup settings");
        }
        finally
        {
            _loading = false;
        }
        RefreshLastAutomatic();
    }

    private void RefreshLastAutomatic()
    {
        var last = backups.LastAutomaticBackup();
        LastAutomaticText = last is { } t
            ? $"Last automatic backup: {t:MMM d, yyyy h:mm tt}. The newest {BackupService.AutomaticKeep} are kept."
            : "No automatic backup yet. One is made each day when Ledger opens.";
    }

    partial void OnIsAutomaticOnChanged(bool value)
    {
        if (!_loading)
        {
            _ = settings.SetAsync(SettingKeys.AutomaticBackups, value ? "1" : "0");
        }
    }

    public Task BackUpNowAsync() => RunAsync(async () =>
    {
        var path = await pickers.PickSaveAsync(backups.SuggestedFileName(), "Ledger backup", ".db");
        if (path is null) return null;
        await Task.Run(() => backups.BackUpToAsync(path));
        return $"Backed up to {path}. Keep a copy somewhere other than this PC, like a USB drive or cloud folder.";
    });

    public Task ExportCsvAsync() => RunAsync(async () =>
    {
        var path = await pickers.PickSaveAsync(exports.SuggestedCsvName(), "CSV (spreadsheet)", ".csv");
        if (path is null) return null;
        await Task.Run(async () =>
        {
            await using var file = File.Create(path);
            await exports.WriteTransactionsCsvAsync(file);
        });
        return $"Transactions exported to {path}.";
    });

    public Task ExportJsonAsync() => RunAsync(async () =>
    {
        var path = await pickers.PickSaveAsync(exports.SuggestedJsonName(), "JSON", ".json");
        if (path is null) return null;
        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        await Task.Run(async () =>
        {
            await using var file = File.Create(path);
            await exports.WriteJsonAsync(file, version);
        });
        return $"Everything exported to {path}.";
    });

    /// <summary>Asks for a backup file and checks it. Returns what's in it, or null (message set) if unusable.</summary>
    public async Task<BackupSummary?> PickBackupAsync()
    {
        Message = null;
        try
        {
            var path = await pickers.PickOpenAsync(".db");
            return path is null ? null : await Task.Run(() => backups.InspectAsync(path));
        }
        catch (LedgerValidationException ex)
        {
            (Message, IsError) = (ex.Message, true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Checking a backup failed");
            (Message, IsError) = ("Ledger couldn't read that file.", true);
        }
        return null;
    }

    /// <summary>Restores, then returns true so the caller restarts Ledger. On failure the current data is untouched or saved.</summary>
    public async Task<bool> RestoreAsync(BackupSummary summary)
    {
        IsBusy = true;
        try
        {
            await Task.Run(() => backups.RestoreAsync(summary.Path));
            return true;
        }
        catch (LedgerValidationException ex)
        {
            (Message, IsError) = (ex.Message, true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Restore failed");
            (Message, IsError) = ($"Restore didn't finish. A copy of your data from just before is in {backups.BackupsFolder}.", true);
        }
        finally
        {
            IsBusy = false;
        }
        return false;
    }

    public void OpenDataFolder() => OpenFolder(AppPaths.DataDirectory);

    public void OpenBackupsFolder()
    {
        Directory.CreateDirectory(backups.BackupsFolder);
        OpenFolder(backups.BackupsFolder);
    }

    private static void OpenFolder(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });

    private async Task RunAsync(Func<Task<string?>> action)
    {
        Message = null;
        IsBusy = true;
        try
        {
            if (await action() is { } done)
            {
                (Message, IsError) = (done, false);
            }
            RefreshLastAutomatic();
        }
        catch (LedgerValidationException ex)
        {
            (Message, IsError) = (ex.Message, true);
        }
        catch (UnauthorizedAccessException)
        {
            (Message, IsError) = ("Ledger can't write there. Choose another folder.", true);
        }
        catch (IOException ex)
        {
            logger.LogError(ex, "Writing a file failed");
            (Message, IsError) = ("Ledger couldn't write that file. It may be open in another program.", true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Data action failed");
            (Message, IsError) = ("Something went wrong. Your data hasn't been changed.", true);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

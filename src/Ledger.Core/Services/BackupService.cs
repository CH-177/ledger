using Ledger.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Ledger.Core.Services;

/// <summary>What a backup file contains, shown before restoring it.</summary>
public sealed record BackupSummary(
    string Path,
    DateTime FileTime,
    int AccountCount,
    int TransactionCount,
    DateOnly? NewestTransaction,
    string SchemaVersion,
    bool NeedsUpgrade);

/// <summary>
/// Backups are complete SQLite databases, the same format Ledger uses itself, so any SQLite tool
/// can open one and the user is never locked in. Restoring always saves the current data first.
/// Logs record counts and file names only, never financial details.
/// </summary>
public sealed class BackupService(
    IDbContextFactory<LedgerDbContext> contextFactory,
    DatabaseInitializer initializer,
    TimeProvider time,
    ILogger<BackupService> logger)
{
    public const string AutomaticFolderName = "Automatic";
    public const int AutomaticKeep = 10;

    /// <summary>Automatic backups are made at most this often.</summary>
    public static readonly TimeSpan AutomaticInterval = TimeSpan.FromHours(20);

    /// <summary>The live database file.</summary>
    public string DatabasePath
    {
        get
        {
            using var db = contextFactory.CreateDbContext();
            return Path.GetFullPath(db.Database.GetDbConnection().DataSource);
        }
    }

    /// <summary>The folder holding automatic backups and pre-upgrade/pre-restore safety copies.</summary>
    public string BackupsFolder => Path.Combine(Path.GetDirectoryName(DatabasePath)!, "Backups");

    public string SuggestedFileName() => $"Ledger backup {time.GetLocalNow():yyyy-MM-dd HHmm}.db";

    /// <summary>
    /// Writes a complete, consistent copy of the database to <paramref name="targetPath"/>, including
    /// anything not yet checkpointed. An existing file there is replaced.
    /// </summary>
    public async Task BackUpToAsync(string targetPath, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(targetPath);
        if (string.Equals(full, DatabasePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new LedgerValidationException("Choose a different file: that's Ledger's live database.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        // VACUUM INTO refuses to overwrite, so write beside the target and swap in when complete.
        var temp = full + ".partial";
        File.Delete(temp);
        await using (var db = await contextFactory.CreateDbContextAsync(ct))
        {
            await db.Database.ExecuteSqlAsync($"VACUUM INTO {temp}", ct);
        }
        File.Move(temp, full, overwrite: true);
        logger.LogInformation("Backup written ({Bytes} bytes)", new FileInfo(full).Length);
    }

    /// <summary>
    /// Makes a daily backup in Backups\Automatic if the newest is older than
    /// <see cref="AutomaticInterval"/>, keeping the newest <see cref="AutomaticKeep"/>. Returns the new
    /// file, or null when a recent one already exists.
    /// </summary>
    public async Task<string?> AutoBackUpAsync(CancellationToken ct = default)
    {
        var folder = Path.Combine(BackupsFolder, AutomaticFolderName);
        Directory.CreateDirectory(folder);
        var existing = new DirectoryInfo(folder).GetFiles("ledger-auto-*.db").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        var now = time.GetUtcNow().UtcDateTime;
        if (existing.Count > 0 && now - existing[0].LastWriteTimeUtc < AutomaticInterval)
        {
            return null;
        }

        var path = Path.Combine(folder, $"ledger-auto-{time.GetLocalNow():yyyyMMdd-HHmmss}.db");
        await BackUpToAsync(path, ct);
        File.SetLastWriteTimeUtc(path, now);
        foreach (var old in new DirectoryInfo(folder).GetFiles("ledger-auto-*.db").OrderByDescending(f => f.LastWriteTimeUtc).Skip(AutomaticKeep))
        {
            old.Delete();
        }
        return path;
    }

    /// <summary>The newest automatic backup's time, if any.</summary>
    public DateTime? LastAutomaticBackup()
    {
        var folder = Path.Combine(BackupsFolder, AutomaticFolderName);
        return Directory.Exists(folder)
            ? new DirectoryInfo(folder).GetFiles("ledger-auto-*.db").Select(f => (DateTime?)f.LastWriteTime).Max()
            : null;
    }

    /// <summary>
    /// Checks a file before restoring it: a readable, intact SQLite database with Ledger's tables,
    /// made by this or an older version. Throws a friendly <see cref="LedgerValidationException"/> otherwise.
    /// </summary>
    public async Task<BackupSummary> InspectAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
        {
            throw new LedgerValidationException("That file no longer exists.");
        }

        var known = await KnownMigrationsAsync(ct);
        try
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            await connection.OpenAsync(ct);

            if (await ScalarAsync<string>(connection, "PRAGMA integrity_check;", ct) != "ok")
            {
                throw new LedgerValidationException("That backup is damaged, so it can't be restored.");
            }
            if (await ScalarAsync<long>(connection,
                    "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('__EFMigrationsHistory', 'Accounts', 'Transactions');", ct) != 3)
            {
                throw new LedgerValidationException("That file isn't a Ledger backup.");
            }

            var applied = new List<string>();
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId;";
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    applied.Add(reader.GetString(0));
                }
            }
            if (applied.Except(known).Any())
            {
                throw new LedgerValidationException("That backup was made by a newer version of Ledger. Update Ledger, then restore it.");
            }

            var newest = await ScalarAsync<string?>(connection, "SELECT MAX(Date) FROM Transactions WHERE IsDeleted = 0;", ct);
            return new BackupSummary(
                path,
                File.GetLastWriteTime(path),
                (int)await ScalarAsync<long>(connection, "SELECT COUNT(*) FROM Accounts;", ct),
                (int)await ScalarAsync<long>(connection, "SELECT COUNT(*) FROM Transactions WHERE IsDeleted = 0;", ct),
                newest is null ? null : DateOnly.Parse(newest, System.Globalization.CultureInfo.InvariantCulture),
                applied.LastOrDefault() ?? "",
                applied.Count < known.Count);
        }
        catch (SqliteException)
        {
            throw new LedgerValidationException("That file isn't a Ledger backup.");
        }
    }

    /// <summary>
    /// Replaces all current data with the backup. The current data is saved first to
    /// Backups\ledger-before-restore-{time}.db. An older backup is then upgraded to this version.
    /// Returns the safety copy's path. The app should restart afterwards so nothing holds old data.
    /// </summary>
    public async Task<string> RestoreAsync(string path, CancellationToken ct = default)
    {
        await InspectAsync(path, ct);

        Directory.CreateDirectory(BackupsFolder);
        var safety = Path.Combine(BackupsFolder, $"ledger-before-restore-{time.GetLocalNow():yyyyMMdd-HHmmss}.db");
        await BackUpToAsync(safety, ct);

        // Copy the backup into the live database page by page (SQLite's online backup), so no file is
        // swapped out from under open connections and the write-ahead log stays consistent.
        SqliteConnection.ClearAllPools();
        await using (var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        await using (var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString()))
        {
            await source.OpenAsync(ct);
            await target.OpenAsync(ct);
            source.BackupDatabase(target);
        }
        SqliteConnection.ClearAllPools();

        await initializer.InitializeAsync(ct); // upgrades an older backup (with its own safety copy)
        logger.LogInformation("Restored a backup; previous data saved as {File}", Path.GetFileName(safety));
        return safety;
    }

    private async Task<IReadOnlyList<string>> KnownMigrationsAsync(CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return db.Database.GetMigrations().ToList();
    }

    private static async Task<T> ScalarAsync<T>(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? default! : (T)value;
    }
}

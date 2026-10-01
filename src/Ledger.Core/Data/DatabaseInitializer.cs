using Ledger.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Ledger.Core.Data;

/// <summary>
/// Brings the database to the latest schema and seeds first-launch data.
/// Safe to run on every startup.
/// </summary>
public sealed class DatabaseInitializer(
    IDbContextFactory<LedgerDbContext> contextFactory,
    ILogger<DatabaseInitializer> logger)
{
    internal const string CategoriesSeededKey = "Seed.DefaultCategories";

    /// <summary>
    /// Applies pending migrations, then seeds default categories exactly once. Before upgrading
    /// an existing database, a complete copy is saved next to it in a "Backups" folder, because
    /// some SQLite schema changes can't run inside a transaction.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        if (pending.Count > 0)
        {
            var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();
            if (applied.Count > 0)
            {
                await BackUpBeforeUpgradeAsync(db, pending[^1], ct);
            }
            logger.LogInformation("Applying {Count} database migration(s)", pending.Count);
        }
        await db.Database.MigrateAsync(ct);

        await SeedCategoriesAsync(db, ct);
    }

    /// <summary>
    /// Saves a consistent copy of the whole database (including anything still in the write-ahead
    /// log) to Backups/ledger-before-{migration}-{time}.db. If the backup fails, the upgrade does
    /// not run: an upgrade without a safety copy is never attempted.
    /// </summary>
    internal async Task<string?> BackUpBeforeUpgradeAsync(LedgerDbContext db, string upgradingTo, CancellationToken ct)
    {
        var source = db.Database.GetDbConnection().DataSource;
        if (string.IsNullOrEmpty(source) || source == ":memory:" || !File.Exists(source))
        {
            return null;
        }

        var folder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source))!, "Backups");
        Directory.CreateDirectory(folder);
        var name = upgradingTo.Split('_', 2) is [_, var label] ? label : upgradingTo;
        var target = Path.Combine(folder, $"ledger-before-{name}-{DateTime.Now:yyyyMMdd-HHmmss}.db");

        // VACUUM INTO writes a complete, compacted copy from a live connection.
        await db.Database.ExecuteSqlAsync($"VACUUM INTO {target}", ct);
        logger.LogInformation("Backed up the database before upgrading to {Migration}", name);
        return target;
    }

    /// <summary>
    /// Copies everything in the write-ahead log into the main database file and
    /// empties the log. Called when the app closes so committed data never depends
    /// on log recovery at the next launch.
    /// </summary>
    public void Checkpoint()
    {
        using var db = contextFactory.CreateDbContext();
        db.Database.ExecuteSqlRaw("PRAGMA wal_checkpoint(TRUNCATE);");
    }

    private static async Task SeedCategoriesAsync(LedgerDbContext db, CancellationToken ct)
    {
        // A marker (rather than "table is empty") means that if the user later
        // archives or reorganises every default, we never re-create them.
        if (await db.Settings.AnyAsync(s => s.Key == CategoriesSeededKey, ct))
        {
            return;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var order = 0;
        foreach (var seed in DefaultCategories.All)
        {
            var parent = new Category
            {
                Name = seed.Name,
                CategoryType = seed.Type,
                Icon = seed.Icon,
                SortOrder = order++,
            };
            var childOrder = 0;
            foreach (var child in seed.Children)
            {
                parent.Children.Add(new Category
                {
                    Name = child,
                    CategoryType = seed.Type,
                    SortOrder = childOrder++,
                });
            }
            db.Categories.Add(parent);
        }

        db.Settings.Add(new AppSetting { Key = CategoriesSeededKey, Value = "1" });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}

using System.Text;
using System.Text.Json;
using Ledger.Core.Data;
using Ledger.Core.Models;
using Ledger.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ledger.Core.Tests;

/// <summary>A migrated database in a real file (backups need files), in its own temp folder.</summary>
internal sealed class FileDatabase : IAsyncDisposable
{
    public DirectoryInfo Folder { get; } = Directory.CreateTempSubdirectory("ledger-backup-");
    public FixedTimeProvider Time { get; } = new(new DateOnly(2026, 9, 27));
    public IDbContextFactory<LedgerDbContext> Factory { get; }
    public DatabaseInitializer Initializer { get; }
    public BackupService Backups { get; }
    public string DatabasePath => Path.Combine(Folder.FullName, "ledger.db");

    public FileDatabase()
    {
        var options = new DbContextOptionsBuilder<LedgerDbContext>().UseSqlite($"Data Source={DatabasePath}").Options;
        Factory = new Factory_(options, Time);
        Initializer = new DatabaseInitializer(Factory, NullLogger<DatabaseInitializer>.Instance);
        Backups = new BackupService(Factory, Initializer, Time, NullLogger<BackupService>.Instance);
    }

    public async Task<Account> AddAccountAsync(string name, long cents = 0)
    {
        await using var db = Factory.CreateDbContext();
        var a = new Account { Name = name, AccountType = AccountType.Checking, StartingBalanceCents = cents };
        db.Accounts.Add(a);
        await db.SaveChangesAsync();
        return a;
    }

    public async Task<List<string>> AccountNamesAsync()
    {
        await using var db = Factory.CreateDbContext();
        return await db.Accounts.OrderBy(a => a.Id).Select(a => a.Name).ToListAsync();
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Folder.Delete(recursive: true);
        return ValueTask.CompletedTask;
    }

    private sealed class Factory_(DbContextOptions<LedgerDbContext> options, TimeProvider time) : IDbContextFactory<LedgerDbContext>
    {
        public LedgerDbContext CreateDbContext() => new(options, time);
    }
}

public class BackupServiceTests
{
    [Fact]
    public async Task A_backup_is_a_complete_database_that_can_be_inspected()
    {
        await using var f = new FileDatabase();
        await f.Initializer.InitializeAsync();
        var checking = await f.AddAccountAsync("Checking", 50000);
        await new TransactionService(f.Factory, f.Time).CreateAsync(new TransactionInput(TransactionType.Expense, 842, new DateOnly(2026, 9, 26), checking.Id));

        var target = Path.Combine(f.Folder.FullName, "elsewhere", "My backup.db");
        await f.Backups.BackUpToAsync(target);
        await f.Backups.BackUpToAsync(target); // replacing an existing backup is fine

        var summary = await f.Backups.InspectAsync(target);
        Assert.Equal((1, 1, new DateOnly(2026, 9, 26), false), (summary.AccountCount, summary.TransactionCount, summary.NewestTransaction, summary.NeedsUpgrade));
        Assert.False(File.Exists(target + ".partial"));
    }

    [Fact]
    public async Task Backing_up_over_the_live_database_is_refused()
    {
        await using var f = new FileDatabase();
        await f.Initializer.InitializeAsync();
        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Backups.BackUpToAsync(f.DatabasePath));
    }

    [Fact]
    public async Task Restoring_replaces_all_data_and_keeps_a_safety_copy_of_what_was_there()
    {
        await using var f = new FileDatabase();
        await f.Initializer.InitializeAsync();
        await f.AddAccountAsync("Checking");
        var backup = Path.Combine(f.Folder.FullName, "backup.db");
        await f.Backups.BackUpToAsync(backup);

        await f.AddAccountAsync("Added after the backup");
        var safety = await f.Backups.RestoreAsync(backup);

        Assert.Equal(["Checking"], await f.AccountNamesAsync());
        var saved = await f.Backups.InspectAsync(safety);
        Assert.Equal(2, saved.AccountCount); // the pre-restore data is recoverable
    }

    [Fact]
    public async Task Restoring_an_older_backup_upgrades_it()
    {
        await using var f = new FileDatabase();
        await f.Initializer.InitializeAsync();

        // A backup made by 1.3.1 (schema up to ShiftBreaks), with an account in it.
        var oldPath = Path.Combine(f.Folder.FullName, "old.db");
        var oldOptions = new DbContextOptionsBuilder<LedgerDbContext>().UseSqlite($"Data Source={oldPath}").Options;
        await using (var old = new LedgerDbContext(oldOptions))
        {
            await old.GetService<IMigrator>().MigrateAsync("ShiftBreaks");
            await old.Database.ExecuteSqlRawAsync("""
                INSERT INTO Accounts (Name, AccountType, StartingBalanceCents, IsIncludedInNetWorth, IsArchived, SortOrder, CreatedAt, UpdatedAt)
                VALUES ('Savings', 1, 56621, 1, 0, 0, '2026-09-27', '2026-09-27');
                """);
        }
        SqliteConnection.ClearAllPools();

        Assert.True((await f.Backups.InspectAsync(oldPath)).NeedsUpgrade);
        await f.Backups.RestoreAsync(oldPath);

        Assert.Equal(["Savings"], await f.AccountNamesAsync());
        await using var db = f.Factory.CreateDbContext();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(0, await db.Goals.CountAsync()); // newest tables exist and work
    }

    [Fact]
    public async Task Files_that_are_not_ledger_backups_are_refused_with_a_clear_reason()
    {
        await using var f = new FileDatabase();
        await f.Initializer.InitializeAsync();

        var text = Path.Combine(f.Folder.FullName, "notes.db");
        await File.WriteAllTextAsync(text, "not a database");
        Assert.Contains("isn't a Ledger backup", (await Assert.ThrowsAsync<LedgerValidationException>(() => f.Backups.InspectAsync(text))).Message);

        var other = Path.Combine(f.Folder.FullName, "other.db");
        await using (var c = new SqliteConnection($"Data Source={other};Pooling=False"))
        {
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE Things (Id INTEGER);";
            await cmd.ExecuteNonQueryAsync();
        }
        Assert.Contains("isn't a Ledger backup", (await Assert.ThrowsAsync<LedgerValidationException>(() => f.Backups.InspectAsync(other))).Message);

        // Made by a newer version: a migration this build doesn't know.
        var newer = Path.Combine(f.Folder.FullName, "newer.db");
        await f.Backups.BackUpToAsync(newer);
        await using (var c = new SqliteConnection($"Data Source={newer};Pooling=False"))
        {
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO __EFMigrationsHistory VALUES ('29990101000000_FromTheFuture', '99.0');";
            await cmd.ExecuteNonQueryAsync();
        }
        Assert.Contains("newer version", (await Assert.ThrowsAsync<LedgerValidationException>(() => f.Backups.InspectAsync(newer))).Message);
        Assert.Contains("newer version", (await Assert.ThrowsAsync<LedgerValidationException>(() => f.Backups.RestoreAsync(newer))).Message);
    }

    [Fact]
    public async Task Automatic_backups_are_daily_and_only_the_newest_ten_are_kept()
    {
        await using var f = new FileDatabase();
        await f.Initializer.InitializeAsync();

        Assert.NotNull(await f.Backups.AutoBackUpAsync());
        Assert.Null(await f.Backups.AutoBackUpAsync()); // already one today

        for (var day = 1; day <= 12; day++)
        {
            f.Time.Today = f.Time.Today.AddDays(1);
            Assert.NotNull(await f.Backups.AutoBackUpAsync());
        }
        var files = Directory.GetFiles(Path.Combine(f.Backups.BackupsFolder, BackupService.AutomaticFolderName), "ledger-auto-*.db");
        Assert.Equal(BackupService.AutomaticKeep, files.Length);
        Assert.NotNull(f.Backups.LastAutomaticBackup());
    }
}

public class ExportServiceTests
{
    private static readonly DateOnly Today = new(2026, 9, 27);

    private static async Task<(TestDatabase Db, ExportService Export)> SeedAsync()
    {
        var db = await TestDatabase.CreateAsync(Today);
        var checking = await db.AddAccountAsync("Checking", AccountType.Checking, 50000);
        var savings = await db.AddAccountAsync("Savings, joint", AccountType.Savings);
        var tx = new TransactionService(db.Factory, db.Time);
        int food;
        await using (var ctx = db.Context())
        {
            food = await ctx.Categories.Where(c => c.Name == "Groceries").Select(c => c.Id).FirstAsync();
        }
        await tx.CreateAsync(new TransactionInput(TransactionType.Income, 16312, new DateOnly(2026, 9, 25), checking.Id, Payee: "Riverside Market"));
        await tx.CreateAsync(new TransactionInput(TransactionType.Expense, 842, new DateOnly(2026, 9, 26), checking.Id, CategoryId: food,
            Payee: "=HYPERLINK(\"http://evil\")", Memo: "said \"hi\"\nsecond line"));
        await tx.CreateAsync(new TransactionInput(TransactionType.Transfer, 5000, new DateOnly(2026, 9, 26), checking.Id, savings.Id));
        var deleted = await tx.CreateAsync(new TransactionInput(TransactionType.Expense, 100, Today, checking.Id, Payee: "Oops"));
        await tx.DeleteAsync(deleted.Id);
        return (db, new ExportService(db.Factory, db.Time));
    }

    [Fact]
    public async Task Csv_is_spreadsheet_safe_and_signed_amounts_follow_the_rules()
    {
        var (db, export) = await SeedAsync();
        await using var _ = db;
        using var stream = new MemoryStream();
        await export.WriteTransactionsCsvAsync(stream);
        var bytes = stream.ToArray();

        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]); // UTF-8 BOM for Excel
        var text = Encoding.UTF8.GetString(bytes[3..]);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Date,Type,Payee,Category,Account,To account,Amount,Signed amount,Status,Note,Recorded automatically", lines[0]);
        Assert.Equal("2026-09-25,Income,Riverside Market,,Checking,,163.12,163.12,Cleared,,No", lines[1]);
        // Formula neutralised, quotes doubled, commas and newlines quoted, category path kept.
        Assert.StartsWith("2026-09-26,Spending,\"'=HYPERLINK(\"\"http://evil\"\")\",Food › Groceries,Checking,,8.42,-8.42,Cleared,\"said \"\"hi\"\"\nsecond line\",No", text.Split("\r\n")[2]);
        Assert.Contains("2026-09-26,Transfer,,,Checking,\"Savings, joint\",50.00,,Cleared,,No", text);
        Assert.DoesNotContain("Oops", text); // deleted transactions aren't exported to CSV
    }

    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+15551234", "'+15551234")]
    [InlineData("-5", "'-5")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("Quickmart", "Quickmart")]
    [InlineData(null, "")]
    public void Text_cells_that_look_like_formulas_are_neutralised(string? input, string expected) =>
        Assert.Equal(expected, ExportService.Text(input));

    [Fact]
    public async Task Json_is_versioned_complete_and_uses_exact_cents()
    {
        var (db, export) = await SeedAsync();
        await using var _ = db;
        using var stream = new MemoryStream();
        await export.WriteJsonAsync(stream, "1.7.0");
        using var json = JsonDocument.Parse(stream.ToArray());
        var root = json.RootElement;

        Assert.Equal("ledger-export", root.GetProperty("format").GetString());
        Assert.Equal(3, root.GetProperty("formatVersion").GetInt32());
        Assert.Equal("1.7.0", root.GetProperty("appVersion").GetString());
        foreach (var section in new[] { "accounts", "categories", "transactions", "budgets", "recurring", "recurringSkips", "goals",
                     "goalContributions", "jobs", "shifts", "paycheckRecords", "jobDeductions", "paystubs",
                     "importBatches", "importProfiles", "importPayeeRules", "reconciliations", "settings" })
        {
            Assert.Equal(JsonValueKind.Array, root.GetProperty(section).ValueKind);
        }

        var transactions = root.GetProperty("transactions").EnumerateArray().ToList();
        Assert.Equal(4, transactions.Count); // including the deleted one, flagged
        var income = transactions[0];
        Assert.Equal(("2026-09-25", "income", 16312L), (income.GetProperty("date").GetString(), income.GetProperty("type").GetString(), income.GetProperty("amountCents").GetInt64()));
        Assert.True(transactions[3].GetProperty("isDeleted").GetBoolean());
        Assert.Equal("savings", root.GetProperty("accounts")[1].GetProperty("type").GetString());
        Assert.Equal(50000, root.GetProperty("accounts")[0].GetProperty("startingBalanceCents").GetInt64());
    }
}

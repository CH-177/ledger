using Ledger.Core.Data;
using Ledger.Core.Models;
using Ledger.Core.Payroll;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Ledger.Core.Tests;

/// <summary>
/// Upgrades real databases created by earlier versions. Anything that rewrites existing data must
/// be proven here, against rows inserted with the old schema.
/// </summary>
public class MigrationTests
{
    [Fact]
    public async Task ShiftBreaks_upgrade_keeps_every_shift_and_converts_hours_correctly()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<LedgerDbContext>().UseSqlite(connection).Options;

        // 1. A database exactly as 1.3.0 left it.
        await using (var db = new LedgerDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync("IncomeCalculator");
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO Accounts (Id, Name, AccountType, StartingBalanceCents, IsIncludedInNetWorth, IsArchived, SortOrder, CreatedAt, UpdatedAt)
                VALUES (1, 'Checking', 0, 50000, 1, 0, 0, '2026-09-27', '2026-09-27');
                INSERT INTO Transactions (Id, AccountId, Date, AmountCents, TransactionType, Status, IsDeleted, CreatedAt, UpdatedAt)
                VALUES (1, 1, '2026-10-02', 4636, 0, 1, 0, '2026-09-27', '2026-09-27');
                INSERT INTO Jobs (Id, Name, PayFrequency, PayPeriodAnchor, PaydayOffsetDays, IsPaydayConfirmed, WorkweekStart, IsOvertimeEligible,
                                  FilingStatus, W4MultipleJobs, W4DependentsCreditCents, W4OtherIncomeCents, W4DeductionsCents, W4ExtraWithholdingCents,
                                  WorkLocationName, WorkNonResidentEitBasisPoints, WorkLstAnnualCents, IsLstExempt, IsArchived, CreatedAt, UpdatedAt)
                VALUES (1, 'Riverside Market', 0, '2026-09-20', 6, 0, 0, 1, 0, 0, 0, 0, 0, 0, 'Riverton City', 50, 5200, 0, 0, '2026-09-27', '2026-09-27');
                INSERT INTO JobRates (Id, JobId, EffectiveDate, HourlyRateCents) VALUES (1, 1, '2025-09-27', 1350);
                -- Pasted as hours: paid minutes, no break.
                INSERT INTO Shifts (Id, JobId, Date, PaidMinutes, UnpaidBreakMinutes, Status, CreatedAt, UpdatedAt) VALUES
                    (1, 1, '2026-09-24', 240, 0, 1, '2026-09-27', '2026-09-27'),
                    (2, 1, '2026-10-02', 360, 0, 0, '2026-09-27', '2026-09-27');
                -- Entered as times with an explicit 30-minute break (11:00–17:30 = 390 − 30 = 360 paid).
                INSERT INTO Shifts (Id, JobId, Date, PaidMinutes, StartTime, EndTime, UnpaidBreakMinutes, Status, Note, CreatedAt, UpdatedAt) VALUES
                    (3, 1, '2026-10-07', 360, '11:00:00', '17:30:00', 30, 0, 'Inventory', '2026-09-27', '2026-09-27'),
                    (4, 1, '2026-10-09', 480, '22:00:00', '06:00:00', 0, 0, NULL, '2026-09-27', '2026-09-27');
                INSERT INTO PaycheckRecords (Id, JobId, PeriodStart, PayDate, EstimatedNetCents, TransactionId)
                VALUES (1, 1, '2026-09-20', '2026-10-02', 4636, 1);
                """);
        }

        // 2. Upgrade to the current schema.
        await using (var db = new LedgerDbContext(options))
        {
            await db.Database.MigrateAsync();
        }

        // 3. Everything survived, converted as intended.
        await using (var db = new LedgerDbContext(options))
        {
            var job = await db.Jobs.Include(j => j.Rates).SingleAsync();
            Assert.Equal(("Riverside Market", 1350L), (job.Name, job.Rates.Single().HourlyRateCents));
            Assert.Equal((30, 300, 15), (job.UnpaidBreakMinutes, job.UnpaidBreakAfterMinutes, job.PaidBreakMinutes));

            var shifts = await db.Shifts.OrderBy(s => s.Id).ToListAsync();
            Assert.Equal([240, 360, 390, 480], shifts.Select(s => s.ScheduledMinutes));
            Assert.Equal([null, null, 30, null], shifts.Select(s => s.BreakOverrideMinutes));
            Assert.Equal("Inventory", shifts[2].Note);
            Assert.Equal(new TimeOnly(11, 0), shifts[2].StartTime);

            // Paid time now: training 4h unchanged; the 6h shift gets the automatic 30 min break;
            // the explicit-break shift pays exactly as before; the 8h overnight gets 30 min off.
            var rule = BreakRule.For(job);
            Assert.Equal([240, 330, 360, 450], shifts.Select(s => rule.PaidMinutesFor(s.ScheduledMinutes, s.BreakOverrideMinutes)));

            var record = await db.PaycheckRecords.SingleAsync();
            Assert.Equal((1, 1), (record.JobId, record.TransactionId));
        }

        // 4. Foreign keys are intact after the table rebuild.
        await AssertNoForeignKeyViolationsAsync(connection);
    }

    [Fact]
    public async Task AccountBalanceDate_upgrade_stops_imports_from_counting_twice()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<LedgerDbContext>().UseSqlite(connection).Options;

        // As the user had it in 2.4.1: accounts added on Sep 26 with the bank's balance that day, then
        // a bank file imported with rows from before (already in that balance) and after.
        await using (var db = new LedgerDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync("ImportOutsideTransferRules");
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO Accounts (Id, Name, AccountType, StartingBalanceCents, IsIncludedInNetWorth, IsArchived, SortOrder, CreatedAt, UpdatedAt)
                VALUES (1, 'Checking', 0, 1047, 1, 0, 0, '2026-09-26 16:00:00.1234567', '2026-09-26 16:00:00.1234567'),
                       (2, 'Savings', 1, 56621, 1, 0, 1, '2026-09-26 16:00:00.1234567', '2026-09-26 16:00:00.1234567'),
                       (3, 'Cash', 2, 12200, 1, 0, 2, '2026-09-26 16:00:00.1234567', '2026-09-26 16:00:00.1234567');
                INSERT INTO ImportBatches (Id, AccountId, FileName, ImportedAt, TransactionCount, IsUndone)
                VALUES (1, 1, 'checking.qfx', '2026-09-29 20:30:00', 3, 0);
                INSERT INTO Transactions (Date, TransactionType, AmountCents, AccountId, TransferAccountId, Status, IsAutoRecorded, IsDeleted, CreatedAt, UpdatedAt, ImportBatchId, Payee)
                VALUES ('2026-09-17', 2, 12000, 2, 1, 1, 0, 0, '2026-09-29', '2026-09-29', 1, 'Kalahari Restaurant - Date'),
                       ('2026-09-25', 1, 200, 1, NULL, 1, 0, 0, '2026-09-29', '2026-09-29', 1, 'Freshco # Date'),
                       ('2026-09-27', 4, 1500, 1, NULL, 1, 0, 0, '2026-09-29', '2026-09-29', 1, 'Wm Supercenter # Main St');
                INSERT INTO Transactions (Date, TransactionType, AmountCents, AccountId, Status, IsAutoRecorded, IsDeleted, CreatedAt, UpdatedAt, Payee)
                VALUES ('2026-09-20', 1, 999, 3, 1, 0, 0, '2026-09-29', '2026-09-29', 'Blind Date');
                """);
        }

        await using (var db = new LedgerDbContext(options))
        {
            await db.Database.MigrateAsync();
        }

        var addedOn = DateOnly.FromDateTime(new DateTime(2026, 9, 26, 16, 0, 0, DateTimeKind.Utc).ToLocalTime());
        await using (var db = new LedgerDbContext(options))
        {
            var dates = await db.Accounts.OrderBy(a => a.Id).Select(a => a.BalanceDate).ToListAsync();
            Assert.Equal([addedOn, addedOn, null], dates); // cash had no imports: unchanged
            var payees = await db.Transactions.OrderBy(t => t.Id).Select(t => t.Payee).ToListAsync();
            Assert.Equal(["Kalahari Restaurant", "Freshco", "Wm Supercenter", "Blind Date"], payees); // typed ones untouched
        }
        var accounts = new Ledger.Core.Services.AccountService(new SingleFactory(options), TimeProvider.System);
        var asOf = new DateOnly(2026, 9, 29);
        Assert.Equal(1047 + 1500, await accounts.GetBalanceAsync(1, asOf));  // only the Sep 27 row moves it
        Assert.Equal(56621, await accounts.GetBalanceAsync(2, asOf));        // the Sep 17 transfer was already in it
        Assert.Equal(12200 - 999, await accounts.GetBalanceAsync(3, asOf));  // no balance date: as before
        await AssertNoForeignKeyViolationsAsync(connection);
    }

    private sealed class SingleFactory(DbContextOptions<LedgerDbContext> options) : IDbContextFactory<LedgerDbContext>
    {
        public LedgerDbContext CreateDbContext() => new(options);
    }

    [Fact]
    public async Task Goals_upgrade_keeps_the_saved_amount_as_the_starting_amount()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<LedgerDbContext>().UseSqlite(connection).Options;

        await using (var db = new LedgerDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync("RecurringSkips");
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO Accounts (Id, Name, AccountType, StartingBalanceCents, IsIncludedInNetWorth, IsArchived, SortOrder, CreatedAt, UpdatedAt)
                VALUES (1, 'Savings', 1, 56621, 1, 0, 0, '2026-09-27', '2026-09-27');
                INSERT INTO Goals (Id, Name, TargetAmountCents, CurrentAmountCents, TargetDate, AccountId, IsArchived, SortOrder, CreatedAt, UpdatedAt)
                VALUES (1, 'MacBook', 120000, 82000, '2027-06-15', 1, 0, 0, '2026-09-27', '2026-09-27');
                """);
        }

        await using (var db = new LedgerDbContext(options))
        {
            await db.Database.MigrateAsync();
        }

        await using (var db = new LedgerDbContext(options))
        {
            var goal = await db.Goals.SingleAsync();
            Assert.Equal(("MacBook", 120000L, 82000L, GoalTracking.Manual, 1), (goal.Name, goal.TargetAmountCents, goal.StartingAmountCents, goal.Tracking, goal.AccountId!.Value));
            Assert.Equal(new DateOnly(2027, 6, 15), goal.TargetDate);
            Assert.Equal(56621, (await db.Accounts.SingleAsync()).StartingBalanceCents);
        }
        await AssertNoForeignKeyViolationsAsync(connection);
    }

    [Fact]
    public async Task BankImport_upgrade_rebuilds_transactions_without_losing_a_row_or_a_link()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<LedgerDbContext>().UseSqlite(connection).Options;

        // A 2.0.0 database with every kind of transaction and everything that points at transactions.
        await using (var db = new LedgerDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync("PaycheckDeductionsAndPaystubs");
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO Accounts (Id, Name, AccountType, StartingBalanceCents, IsIncludedInNetWorth, IsArchived, SortOrder, CreatedAt, UpdatedAt) VALUES
                    (1, 'Checking', 0, 50000, 1, 0, 0, '2026-09-27', '2026-09-27'),
                    (2, 'Savings', 1, 56621, 1, 0, 1, '2026-09-27', '2026-09-27');
                INSERT INTO RecurringTransactions (Id, Name, AmountCents, IsAmountEstimated, Frequency, TransactionType, AccountId, StartDate, IsActive, CreatedAt, UpdatedAt)
                    VALUES (1, 'Spotify', 1199, 0, 2, 1, 1, '2026-09-27', 1, '2026-09-27', '2026-09-27');
                INSERT INTO Transactions (Id, AccountId, TransferAccountId, Date, AmountCents, TransactionType, Payee, Memo, Status, RecurringTransactionId, RecurrenceDate, IsAutoRecorded, ExternalId, IsDeleted, DeletedAt, CreatedAt, UpdatedAt) VALUES
                    (1, 1, NULL, '2026-10-09', 16312, 0, 'Riverside Market', 'first paycheck', 1, NULL, NULL, 0, NULL, 0, NULL, '2026-09-27', '2026-09-27'),
                    (2, 1, NULL, '2026-09-27', 1199, 1, 'Spotify', NULL, 1, 1, '2026-09-27', 1, NULL, 0, NULL, '2026-09-27', '2026-09-27'),
                    (3, 1, 2, '2026-09-28', 5000, 2, NULL, NULL, 0, NULL, NULL, 0, 'ext-7', 0, NULL, '2026-09-27', '2026-09-27'),
                    (4, 1, NULL, '2026-09-26', 842, 1, 'Deleted', NULL, 1, NULL, NULL, 0, NULL, 1, '2026-09-27', '2026-09-27', '2026-09-27');
                INSERT INTO Jobs (Id, Name, PayFrequency, PayPeriodAnchor, PaydayOffsetDays, IsPaydayConfirmed, WorkweekStart, IsOvertimeEligible,
                                  FilingStatus, W4MultipleJobs, W4DependentsCreditCents, W4OtherIncomeCents, W4DeductionsCents, W4ExtraWithholdingCents,
                                  WorkLocationName, WorkNonResidentEitBasisPoints, WorkLstAnnualCents, IsLstExempt, IsArchived, CreatedAt, UpdatedAt)
                    VALUES (1, 'Riverside Market', 0, '2026-09-20', 6, 0, 0, 1, 0, 0, 0, 0, 0, 0, 'Riverton City', 50, 5200, 0, 0, '2026-09-27', '2026-09-27');
                INSERT INTO PaycheckRecords (Id, JobId, PeriodStart, PayDate, EstimatedNetCents, TransactionId) VALUES (1, 1, '2026-09-27', '2026-10-09', 15885, 1);
                """);
        }
        var before = await DumpTransactionsAsync(connection);

        await using (var db = new LedgerDbContext(options))
        {
            await db.Database.MigrateAsync();
        }

        // Every column of every row survives the rebuild; the new column is empty.
        Assert.Equal(before, await DumpTransactionsAsync(connection, "ImportBatchId", "ReconciliationId", "PaidFromGoalId") /* columns added by later migrations */);
        await using (var db = new LedgerDbContext(options))
        {
            var all = await db.Transactions.IgnoreQueryFilters().OrderBy(t => t.Id).ToListAsync();
            Assert.Equal(4, all.Count);
            Assert.All(all, t => Assert.Null(t.ImportBatchId));
            Assert.Equal(1, (await db.PaycheckRecords.SingleAsync()).TransactionId);
            Assert.Equal((1, (DateOnly?)new DateOnly(2026, 9, 27)), (all[1].RecurringTransactionId!.Value, all[1].RecurrenceDate));
        }
        await AssertNoForeignKeyViolationsAsync(connection);
    }

    private static async Task<string> DumpTransactionsAsync(SqliteConnection connection, params string[] skipColumns)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM Transactions ORDER BY Id;";
        await using var r = await cmd.ExecuteReaderAsync();
        var sb = new System.Text.StringBuilder();
        while (await r.ReadAsync())
        {
            // A rebuild may reorder columns, so compare by name, not position.
            var cells = Enumerable.Range(0, r.FieldCount)
                .Where(i => !skipColumns.Contains(r.GetName(i)))
                .Select(i => $"{r.GetName(i)}={r.GetValue(i)}")
                .OrderBy(c => c, StringComparer.Ordinal);
            sb.AppendLine(string.Join("|", cells));
        }
        return sb.ToString();
    }

    [Fact]
    public async Task HomeTaxDefault_upgrade_leaves_settings_as_they_are()
    {
        async Task<Dictionary<string, string>> UpgradeAsync(string seedSql)
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<LedgerDbContext>().UseSqlite(connection).Options;
            await using var db = new LedgerDbContext(options);
            await db.GetService<IMigrator>().MigrateAsync("SinkingFunds");
            if (seedSql.Length > 0)
            {
                await db.Database.ExecuteSqlRawAsync(seedSql);
            }
            await db.Database.MigrateAsync();
            return await db.Settings.AsNoTracking().Where(s => s.Key.StartsWith("Income.")).ToDictionaryAsync(s => s.Key, s => s.Value);
        }
        const string Account = """
            INSERT INTO Accounts (Id, Name, AccountType, StartingBalanceCents, IsIncludedInNetWorth, IsArchived, SortOrder, CreatedAt, UpdatedAt)
            VALUES (1, 'Checking', 0, 50000, 1, 0, 0, '2026-09-27', '2026-09-27');
            """;

        // Older data without a saved home stays without one (it now means "not set yet").
        Assert.Empty(await UpgradeAsync(Account));

        // A brand-new database starts with nothing set.
        Assert.Empty(await UpgradeAsync(""));

        // A home the user saved is never touched.
        var saved = await UpgradeAsync(Account + """
            INSERT INTO Settings (Key, Value) VALUES ('Income.HomeLocationName', 'Oak Twp'), ('Income.HomePsdCode', '990102'),
                                                     ('Income.HomeResidentEitBasisPoints', '150');
            """);
        Assert.Equal("Oak Twp", saved["Income.HomeLocationName"]);
        Assert.Equal("990102", saved["Income.HomePsdCode"]);
    }

    [Fact]
    public async Task Upgrading_a_file_database_saves_a_full_backup_first()
    {
        var dir = Directory.CreateTempSubdirectory("ledger-migrate-");
        var path = Path.Combine(dir.FullName, "ledger.db");
        try
        {
            var options = new DbContextOptionsBuilder<LedgerDbContext>().UseSqlite($"Data Source={path}").Options;
            await using (var db = new LedgerDbContext(options))
            {
                await db.GetService<IMigrator>().MigrateAsync("IncomeCalculator");
                await db.Database.ExecuteSqlRawAsync("""
                    INSERT INTO Accounts (Id, Name, AccountType, StartingBalanceCents, IsIncludedInNetWorth, IsArchived, SortOrder, CreatedAt, UpdatedAt)
                    VALUES (1, 'Savings', 1, 56621, 1, 0, 0, '2026-09-27', '2026-09-27');
                    """);
            }

            var factory = new FileFactory(options);
            await new DatabaseInitializer(factory, Microsoft.Extensions.Logging.Abstractions.NullLogger<DatabaseInitializer>.Instance).InitializeAsync();

            var backup = Assert.Single(Directory.GetFiles(Path.Combine(dir.FullName, "Backups"), "ledger-before-*.db"));
            await using var check = new SqliteConnection($"Data Source={backup};Mode=ReadOnly");
            await check.OpenAsync();
            await using var cmd = check.CreateCommand();
            cmd.CommandText = "SELECT StartingBalanceCents FROM Accounts WHERE Name = 'Savings'";
            Assert.Equal(56621L, await cmd.ExecuteScalarAsync());
            cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Shifts') WHERE name = 'PaidMinutes'";
            Assert.Equal(1L, await cmd.ExecuteScalarAsync()); // the backup is the pre-upgrade schema
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_brand_new_database_needs_no_backup()
    {
        var dir = Directory.CreateTempSubdirectory("ledger-migrate-");
        try
        {
            var options = new DbContextOptionsBuilder<LedgerDbContext>().UseSqlite($"Data Source={Path.Combine(dir.FullName, "ledger.db")}").Options;
            await new DatabaseInitializer(new FileFactory(options), Microsoft.Extensions.Logging.Abstractions.NullLogger<DatabaseInitializer>.Instance).InitializeAsync();
            Assert.False(Directory.Exists(Path.Combine(dir.FullName, "Backups")));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            dir.Delete(recursive: true);
        }
    }

    private sealed class FileFactory(DbContextOptions<LedgerDbContext> options) : IDbContextFactory<LedgerDbContext>
    {
        public LedgerDbContext CreateDbContext() => new(options);
    }

    private static async Task AssertNoForeignKeyViolationsAsync(SqliteConnection connection)
    {
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA foreign_key_check;";
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.False(await reader.ReadAsync(), "foreign_key_check reported violations");
        }
    }
}

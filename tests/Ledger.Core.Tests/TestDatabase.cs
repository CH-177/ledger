using Ledger.Core.Data;
using Ledger.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ledger.Core.Tests;

/// <summary>
/// A real, migrated SQLite database held in memory for the lifetime of one test.
/// Uses the production migrations so constraints and foreign keys are exercised.
/// </summary>
internal sealed class TestDatabase : IAsyncDisposable
{
    private readonly SqliteConnection _connection;

    public IDbContextFactory<LedgerDbContext> Factory { get; }
    public FixedTimeProvider Time { get; }

    private TestDatabase(SqliteConnection connection, FixedTimeProvider time)
    {
        _connection = connection;
        Time = time;
        var options = new DbContextOptionsBuilder<LedgerDbContext>().UseSqlite(connection).Options;
        Factory = new ContextFactory(options, time);
    }

    public static async Task<TestDatabase> CreateAsync(DateOnly? today = null)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new TestDatabase(connection, new FixedTimeProvider(today ?? new DateOnly(2026, 9, 26)));
        await new DatabaseInitializer(db.Factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();
        return db;
    }

    public LedgerDbContext Context() => Factory.CreateDbContext();

    public async Task<Account> AddAccountAsync(string name, AccountType type, long startingCents = 0)
    {
        await using var db = Context();
        var account = new Account { Name = name, AccountType = type, StartingBalanceCents = startingCents };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return account;
    }

    public async Task<Transaction> AddTransactionAsync(Transaction transaction)
    {
        await using var db = Context();
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync();
        return transaction;
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    private sealed class ContextFactory(DbContextOptions<LedgerDbContext> options, TimeProvider time)
        : IDbContextFactory<LedgerDbContext>
    {
        public LedgerDbContext CreateDbContext() => new(options, time);
    }
}

/// <summary>A clock frozen at noon local time on a given date.</summary>
internal sealed class FixedTimeProvider(DateOnly today) : TimeProvider
{
    public DateOnly Today { get; set; } = today;

    public override DateTimeOffset GetUtcNow() =>
        new DateTimeOffset(Today.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}

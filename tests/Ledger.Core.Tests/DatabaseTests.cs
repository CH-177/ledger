using Ledger.Core.Data;
using Ledger.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ledger.Core.Tests;

public class DatabaseTests
{
    [Fact]
    public async Task Fresh_database_has_default_categories_and_no_financial_data()
    {
        await using var t = await TestDatabase.CreateAsync();
        await using var db = t.Context();

        Assert.Contains(await db.Categories.ToListAsync(), c => c.Name == "Groceries");
        Assert.Empty(await db.Accounts.ToListAsync());
        Assert.Empty(await db.Transactions.ToListAsync());
    }

    [Fact]
    public async Task Default_categories_are_hierarchical()
    {
        await using var t = await TestDatabase.CreateAsync();
        await using var db = t.Context();

        var food = await db.Categories.Include(c => c.Children).SingleAsync(c => c.Name == "Food");
        Assert.Null(food.ParentCategoryId);
        Assert.Equal(["Groceries", "Restaurants", "Fast Food"], food.Children.OrderBy(c => c.SortOrder).Select(c => c.Name));
        Assert.All(food.Children, c => Assert.Equal(CategoryType.Expense, c.CategoryType));
    }

    [Fact]
    public async Task Initializing_twice_does_not_duplicate_categories()
    {
        await using var t = await TestDatabase.CreateAsync();
        int before;
        await using (var db = t.Context()) before = await db.Categories.CountAsync();

        await new DatabaseInitializer(t.Factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();

        await using (var db = t.Context()) Assert.Equal(before, await db.Categories.CountAsync());
    }

    [Fact]
    public async Task Deleted_defaults_are_not_reseeded()
    {
        await using var t = await TestDatabase.CreateAsync();
        await using (var db = t.Context())
        {
            await db.Categories.Where(c => c.ParentCategoryId != null).ExecuteDeleteAsync();
            await db.Categories.ExecuteDeleteAsync();
        }

        await new DatabaseInitializer(t.Factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();

        await using (var db = t.Context()) Assert.Equal(0, await db.Categories.CountAsync());
    }

    [Fact]
    public async Task Checkpoint_moves_committed_data_into_the_main_file()
    {
        var dir = Directory.CreateTempSubdirectory("ledger-test-");
        var path = Path.Combine(dir.FullName, "ledger.db");
        try
        {
            var factory = new FileContextFactory(path);
            var initializer = new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance);
            await initializer.InitializeAsync();
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
                db.Accounts.Add(new Account { Name = "Savings", AccountType = AccountType.Savings, StartingBalanceCents = 56621 });
                await db.SaveChangesAsync();
            }

            initializer.Checkpoint();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            var wal = new FileInfo(path + "-wal");
            Assert.True(!wal.Exists || wal.Length == 0, "WAL should be empty after checkpoint");

            // Reading the main file alone (log removed) still finds the account.
            if (wal.Exists) wal.Delete();
            await using var check = factory.CreateDbContext();
            Assert.Equal(56621, (await check.Accounts.SingleAsync()).StartingBalanceCents);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            dir.Delete(recursive: true);
        }
    }

    private sealed class FileContextFactory(string path) : IDbContextFactory<LedgerDbContext>
    {
        public LedgerDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<LedgerDbContext>().UseSqlite($"Data Source={path}").Options);
    }

    [Fact]
    public async Task Timestamps_are_set_on_create_and_update()
    {
        await using var t = await TestDatabase.CreateAsync(new DateOnly(2026, 1, 1));
        var account = await t.AddAccountAsync("Checking", AccountType.Checking);
        Assert.Equal(new DateTime(2026, 1, 1, 12, 0, 0), account.CreatedAt);

        t.Time.Today = new DateOnly(2026, 2, 1);
        await using var db = t.Context();
        var loaded = await db.Accounts.SingleAsync();
        loaded.Name = "Everyday";
        await db.SaveChangesAsync();

        Assert.Equal(new DateTime(2026, 1, 1, 12, 0, 0), loaded.CreatedAt);
        Assert.Equal(new DateTime(2026, 2, 1, 12, 0, 0), loaded.UpdatedAt);
    }

    [Fact]
    public async Task Zero_or_negative_amounts_are_rejected_by_the_database()
    {
        await using var t = await TestDatabase.CreateAsync();
        var a = await t.AddAccountAsync("Checking", AccountType.Checking);

        await Assert.ThrowsAsync<DbUpdateException>(() => t.AddTransactionAsync(new Transaction
        {
            AccountId = a.Id, AmountCents = 0, TransactionType = TransactionType.Expense, Date = new(2026, 9, 1),
        }));
        await Assert.ThrowsAsync<DbUpdateException>(() => t.AddTransactionAsync(new Transaction
        {
            AccountId = a.Id, AmountCents = -100, TransactionType = TransactionType.Expense, Date = new(2026, 9, 1),
        }));
    }

    [Fact]
    public async Task Transfer_requires_a_different_destination_account()
    {
        await using var t = await TestDatabase.CreateAsync();
        var a = await t.AddAccountAsync("Checking", AccountType.Checking);

        // Missing destination
        await Assert.ThrowsAsync<DbUpdateException>(() => t.AddTransactionAsync(new Transaction
        {
            AccountId = a.Id, AmountCents = 100, TransactionType = TransactionType.Transfer, Date = new(2026, 9, 1),
        }));
        // Transfer to itself
        await Assert.ThrowsAsync<DbUpdateException>(() => t.AddTransactionAsync(new Transaction
        {
            AccountId = a.Id, TransferAccountId = a.Id, AmountCents = 100,
            TransactionType = TransactionType.Transfer, Date = new(2026, 9, 1),
        }));
    }

    [Fact]
    public async Task Non_transfers_cannot_name_a_destination_account()
    {
        await using var t = await TestDatabase.CreateAsync();
        var a = await t.AddAccountAsync("Checking", AccountType.Checking);
        var b = await t.AddAccountAsync("Savings", AccountType.Savings);

        await Assert.ThrowsAsync<DbUpdateException>(() => t.AddTransactionAsync(new Transaction
        {
            AccountId = a.Id, TransferAccountId = b.Id, AmountCents = 100,
            TransactionType = TransactionType.Expense, Date = new(2026, 9, 1),
        }));
    }

    [Fact]
    public async Task Foreign_keys_are_enforced()
    {
        await using var t = await TestDatabase.CreateAsync();
        await Assert.ThrowsAsync<DbUpdateException>(() => t.AddTransactionAsync(new Transaction
        {
            AccountId = 9999, AmountCents = 100, TransactionType = TransactionType.Expense, Date = new(2026, 9, 1),
        }));
    }

    [Fact]
    public async Task Accounts_with_history_cannot_be_hard_deleted()
    {
        await using var t = await TestDatabase.CreateAsync();
        var a = await t.AddAccountAsync("Checking", AccountType.Checking);
        await t.AddTransactionAsync(new Transaction
        {
            AccountId = a.Id, AmountCents = 100, TransactionType = TransactionType.Expense, Date = new(2026, 9, 1),
        });

        await using var db = t.Context();
        await Assert.ThrowsAsync<DbUpdateException>(async () =>
        {
            db.Accounts.Remove(await db.Accounts.SingleAsync());
            await db.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task A_recurring_occurrence_can_only_be_posted_once()
    {
        await using var t = await TestDatabase.CreateAsync();
        var a = await t.AddAccountAsync("Checking", AccountType.Checking);
        int ruleId;
        await using (var db = t.Context())
        {
            var rule = new RecurringTransaction
            {
                Name = "Spotify", AmountCents = 1199, Frequency = RecurrenceFrequency.Monthly,
                TransactionType = TransactionType.Expense, AccountId = a.Id, StartDate = new(2026, 1, 15),
            };
            db.RecurringTransactions.Add(rule);
            await db.SaveChangesAsync();
            ruleId = rule.Id;
        }

        Transaction Occurrence() => new()
        {
            AccountId = a.Id, AmountCents = 1199, TransactionType = TransactionType.Expense,
            Date = new(2026, 9, 15), RecurringTransactionId = ruleId, RecurrenceDate = new(2026, 9, 15),
        };

        await t.AddTransactionAsync(Occurrence());
        await Assert.ThrowsAsync<DbUpdateException>(() => t.AddTransactionAsync(Occurrence()));
    }

    [Fact]
    public async Task Budget_month_is_unique_and_valid()
    {
        await using var t = await TestDatabase.CreateAsync();
        await using (var db = t.Context())
        {
            db.Budgets.Add(new Budget { Year = 2026, Month = 9 });
            await db.SaveChangesAsync();
        }
        await using (var db = t.Context())
        {
            db.Budgets.Add(new Budget { Year = 2026, Month = 9 });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        await using (var db = t.Context())
        {
            db.Budgets.Add(new Budget { Year = 2026, Month = 13 });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task Soft_deleted_transactions_are_hidden_but_retained()
    {
        await using var t = await TestDatabase.CreateAsync();
        var a = await t.AddAccountAsync("Checking", AccountType.Checking);
        await t.AddTransactionAsync(new Transaction
        {
            AccountId = a.Id, AmountCents = 100, TransactionType = TransactionType.Expense,
            Date = new(2026, 9, 1), IsDeleted = true,
        });

        await using var db = t.Context();
        Assert.Equal(0, await db.Transactions.CountAsync());
        Assert.Equal(1, await db.Transactions.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task Dates_round_trip_across_month_year_and_leap_boundaries()
    {
        await using var t = await TestDatabase.CreateAsync();
        var a = await t.AddAccountAsync("Checking", AccountType.Checking);
        DateOnly[] dates = [new(2024, 2, 29), new(2025, 12, 31), new(2026, 1, 1), new(2026, 2, 28)];
        foreach (var d in dates)
        {
            await t.AddTransactionAsync(new Transaction
            {
                AccountId = a.Id, AmountCents = 100, TransactionType = TransactionType.Expense, Date = d,
            });
        }

        await using var db = t.Context();
        Assert.Equal(dates, await db.Transactions.OrderBy(x => x.Date).Select(x => x.Date).ToListAsync());

        // Range queries on stored dates must be chronological, not lexical surprises.
        var jan2026 = await db.Transactions
            .Where(x => x.Date >= new DateOnly(2026, 1, 1) && x.Date <= new DateOnly(2026, 1, 31))
            .CountAsync();
        Assert.Equal(1, jan2026);
    }
}

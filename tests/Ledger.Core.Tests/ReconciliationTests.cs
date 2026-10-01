using Ledger.Core.Models;
using Ledger.Core.Services;

namespace Ledger.Core.Tests;

public class ReconciliationTests
{
    private static readonly DateOnly Today = new(2026, 11, 5);
    private static DateOnly D(int m, int d) => new(2026, m, d);

    private sealed class Fixture : IAsyncDisposable
    {
        public required TestDatabase Db { get; init; }
        public required TransactionService Transactions { get; init; }
        public required ReconciliationService Reconcile { get; init; }
        public required Account Checking { get; init; }
        public ValueTask DisposeAsync() => Db.DisposeAsync();

        public async Task<int> Add(TransactionType type, long cents, DateOnly date, string payee, TransactionStatus status = TransactionStatus.Cleared, Account? account = null, Account? to = null) =>
            (await Transactions.CreateAsync(new TransactionInput(type, cents, date, (account ?? Checking).Id, to?.Id, Payee: payee, Status: status))).Id;
    }

    private static async Task<Fixture> CreateAsync()
    {
        var db = await TestDatabase.CreateAsync(Today);
        return new Fixture
        {
            Db = db,
            Transactions = new TransactionService(db.Factory, db.Time),
            Reconcile = new ReconciliationService(db.Factory, db.Time),
            Checking = await db.AddAccountAsync("Checking", AccountType.Checking, 10000),
        };
    }

    [Fact]
    public async Task The_difference_follows_every_tick_and_only_zero_finishes_then_the_next_statement_starts_from_it()
    {
        await using var f = await CreateAsync();
        var gas = await f.Add(TransactionType.Expense, 842, D(9, 28), "Quickmart");
        var pay = await f.Add(TransactionType.Income, 16312, D(9, 30), "Riverside Market");
        var pending = await f.Add(TransactionType.Expense, 2000, D(10, 1), "Valumart", TransactionStatus.Pending);
        var late = await f.Add(TransactionType.Expense, 500, D(10, 3), "Wawa");

        var start = await f.Reconcile.StartAsync(f.Checking.Id, D(9, 30));
        Assert.Equal(10000, start.ReconciledBalanceCents);
        Assert.Equal([gas, pay, pending, late], start.Items.Select(i => i.TransactionId));
        Assert.Equal([false, false, true, true], start.Items.Select(i => i.AfterStatement));
        Assert.Equal([gas, pay], start.DefaultTicked.Order()); // cleared, on or before the date

        const long statement = 10000 - 842 + 16312;
        Assert.Equal(16312 - 842, start.DifferenceCents(statement, new HashSet<int>()));
        Assert.Equal(16312, start.DifferenceCents(statement, new HashSet<int> { gas }));
        Assert.Equal(0, start.DifferenceCents(statement, new HashSet<int> { gas, pay }));

        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Reconcile.FinishAsync(f.Checking.Id, D(9, 30), statement, [gas]));
        await f.Reconcile.FinishAsync(f.Checking.Id, D(9, 30), statement, [gas, pay]);

        // October's statement starts from September's balance; the pending one is left for you to tick.
        var october = await f.Reconcile.StartAsync(f.Checking.Id, D(10, 31));
        Assert.Equal(statement, october.ReconciledBalanceCents);
        Assert.Equal([pending, late], october.Items.Select(i => i.TransactionId));
        Assert.Equal([late], october.DefaultTicked);
        const long octoberStatement = statement - 2000 - 500;
        Assert.Contains("pending", Assert.Single(ReconciliationService.Hints(october, octoberStatement, october.DefaultTicked)));
        await f.Reconcile.FinishAsync(f.Checking.Id, D(10, 31), octoberStatement, [pending, late]);

        Assert.Equal([D(10, 31), D(9, 30)], (await f.Reconcile.GetHistoryAsync(f.Checking.Id)).Select(r => r.StatementDate));
        Assert.Equal(TransactionStatus.Reconciled, (await f.Transactions.GetAsync(pending)).Status);

        // Undo takes back only the latest one.
        await f.Reconcile.UndoLastAsync(f.Checking.Id);
        Assert.Equal(TransactionStatus.Cleared, (await f.Transactions.GetAsync(pending)).Status);
        Assert.Equal(TransactionStatus.Reconciled, (await f.Transactions.GetAsync(gas)).Status);
        Assert.Single(await f.Reconcile.GetHistoryAsync(f.Checking.Id));
    }

    [Fact]
    public async Task Hints_point_at_a_swapped_amount_a_late_posting_and_something_not_on_the_statement()
    {
        await using var f = await CreateAsync();
        var valumart = await f.Add(TransactionType.Expense, 4510, D(9, 12), "Valumart");   // statement says $54.10
        var later = await f.Add(TransactionType.Expense, 1199, D(10, 2), "Spotify");  // posted Sep 30 by the bank
        var extra = await f.Add(TransactionType.Expense, 300, D(9, 20), "Vending");  // not on the statement
        var start = await f.Reconcile.StartAsync(f.Checking.Id, D(9, 30));

        var swapped = ReconciliationService.Hints(start, 10000 - 5410, new HashSet<int> { valumart });
        Assert.Contains(swapped, h => h.Contains("$54.10") && h.Contains("swapped"));

        var late = ReconciliationService.Hints(start, 10000 - 4510 - 1199, new HashSet<int> { valumart });
        Assert.Contains(late, h => h.Contains("Spotify") && h.Contains("after the statement"));

        var notOnIt = ReconciliationService.Hints(start, 10000 - 4510, new HashSet<int> { valumart, extra });
        Assert.Contains(notOnIt, h => h.Contains("Vending") && h.Contains("unticking"));
        Assert.Empty(ReconciliationService.Hints(start, 10000 - 4510 - 300, new HashSet<int> { valumart, extra }));
        _ = later;
    }

    [Fact]
    public async Task Transactions_inside_the_starting_balance_are_not_offered_and_transfers_count_on_both_sides()
    {
        await using var f = await CreateAsync();
        var accounts = new AccountService(f.Db.Factory, f.Db.Time);
        await accounts.UpdateAsync(f.Checking.Id, new AccountInput("Checking", AccountType.Checking, 1047, BalanceDate: D(9, 26)));
        var savings = await f.Db.AddAccountAsync("Savings", AccountType.Savings, 56621);
        await f.Add(TransactionType.Expense, 200, D(9, 25), "Freshco");                       // already in the $10.47
        var move = await f.Add(TransactionType.Transfer, 1200, D(10, 3), "Transfer", account: savings, to: f.Checking);

        var checking = await f.Reconcile.StartAsync(f.Checking.Id, D(10, 31));
        Assert.Equal((1047L, move, 1200L), (checking.ReconciledBalanceCents, Assert.Single(checking.Items).TransactionId, checking.Items[0].EffectCents));
        var fromSavings = await f.Reconcile.StartAsync(savings.Id, D(10, 31));
        Assert.Equal(-1200, Assert.Single(fromSavings.Items).EffectCents);
    }

    [Fact]
    public async Task A_card_statement_is_money_owed()
    {
        await using var f = await CreateAsync();
        var card = await f.Db.AddAccountAsync("Card", AccountType.CreditCard);
        var a = await f.Add(TransactionType.Expense, 2500, D(9, 5), "Amazon", account: card);
        var b = await f.Add(TransactionType.Refund, 500, D(9, 9), "Amazon", account: card);
        var start = await f.Reconcile.StartAsync(card.Id, D(9, 30));
        Assert.Equal(0, start.DifferenceCents(-2000, new HashSet<int> { a, b })); // the statement says $20.00 owed
        await f.Reconcile.FinishAsync(card.Id, D(9, 30), -2000, [a, b]);
    }

    [Theory]
    [InlineData(4510L, new long[] { 4150, 4501, 5410 })]
    [InlineData(1000L, new long[0])] // "0100" isn't an amount anyone typed; "00" swaps change nothing
    public void Adjacent_swaps(long cents, long[] expected) =>
        Assert.Equal(expected, ReconciliationService.AdjacentSwaps(cents).Order());
}

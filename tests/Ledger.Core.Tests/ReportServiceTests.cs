using Ledger.Core.Models;
using Ledger.Core.Services;

namespace Ledger.Core.Tests;

public class ReportServiceTests
{
    private static readonly DateOnly Today = new(2026, 9, 27);

    private static async Task<(TestDatabase Db, ReportService Reports, TransactionService Tx, Account Checking, Account Card)> CreateAsync()
    {
        var db = await TestDatabase.CreateAsync(Today);
        var checking = await db.AddAccountAsync("Checking", AccountType.Checking, 100000);
        var card = await db.AddAccountAsync("Card", AccountType.CreditCard);
        var tx = new TransactionService(db.Factory, db.Time);
        return (db, new ReportService(tx, new AccountService(db.Factory, db.Time)), tx, checking, card);
    }

    [Fact]
    public async Task Cash_flow_covers_whole_past_months_and_this_month_through_today()
    {
        var (db, reports, tx, checking, card) = await CreateAsync();
        await using var _ = db;
        await tx.CreateAsync(new TransactionInput(TransactionType.Income, 50000, new DateOnly(2026, 7, 31), checking.Id));
        await tx.CreateAsync(new TransactionInput(TransactionType.Expense, 20000, new DateOnly(2026, 8, 1), checking.Id));
        await tx.CreateAsync(new TransactionInput(TransactionType.Expense, 3000, new DateOnly(2026, 9, 10), card.Id));
        await tx.CreateAsync(new TransactionInput(TransactionType.Transfer, 3000, new DateOnly(2026, 9, 20), checking.Id, card.Id)); // not spending
        await tx.CreateAsync(new TransactionInput(TransactionType.Expense, 9999, new DateOnly(2026, 9, 30), checking.Id));          // after today

        var flow = await reports.GetCashFlowAsync(Today, 3);

        Assert.Equal([(7, 50000L, 0L, false), (8, 0L, 20000L, false), (9, 0L, 3000L, true)],
            flow.Select(f => (f.Month, f.IncomeCents, f.ExpenseCents, f.IsPartial)));
        Assert.Equal(-20000, flow[1].NetCents);
    }

    [Fact]
    public async Task Cash_flow_crosses_year_boundaries()
    {
        var (db, reports, _, _, _) = await CreateAsync();
        await using var __ = db;
        var flow = await reports.GetCashFlowAsync(new DateOnly(2027, 2, 3), 4);
        Assert.Equal([(2026, 11), (2026, 12), (2027, 1), (2027, 2)], flow.Select(f => (f.Year, f.Month)));
    }

    [Fact]
    public async Task Net_worth_history_is_taken_at_each_month_end_and_today()
    {
        var (db, reports, tx, checking, card) = await CreateAsync();
        await using var _ = db;
        await tx.CreateAsync(new TransactionInput(TransactionType.Expense, 10000, new DateOnly(2026, 8, 15), card.Id));   // owed $100
        await tx.CreateAsync(new TransactionInput(TransactionType.Income, 25000, new DateOnly(2026, 9, 5), checking.Id));

        var history = await reports.GetNetWorthHistoryAsync(Today, 3);

        Assert.Equal([new DateOnly(2026, 7, 31), new DateOnly(2026, 8, 31), Today], history.Select(h => h.AsOf));
        Assert.Equal([100000L, 90000L, 115000L], history.Select(h => h.NetWorthCents));
        Assert.Equal(10000, history[1].LiabilitiesCents);
    }
}

using Ledger.Core.Models;
using Ledger.Core.Services;

namespace Ledger.Core.Tests;

public class DashboardServiceTests
{
    private static readonly DateOnly Today = new(2026, 9, 27);
    private static DateOnly D(int m, int d) => new(2026, m, d);

    [Fact]
    public async Task This_month_counts_income_spending_and_net_saving_through_today()
    {
        await using var db = await TestDatabase.CreateAsync(Today);
        var checking = await db.AddAccountAsync("Checking", AccountType.Checking, 50000);
        var savings = await db.AddAccountAsync("Savings", AccountType.Savings, 50000);
        var savings2 = await db.AddAccountAsync("High yield", AccountType.Savings);
        var card = await db.AddAccountAsync("Card", AccountType.CreditCard);
        var tx = new TransactionService(db.Factory, db.Time);
        async Task Add(TransactionType type, long cents, DateOnly date, int from, int? to = null) =>
            await tx.CreateAsync(new TransactionInput(type, cents, date, from, to));

        await Add(TransactionType.Income, 61240, D(9, 5), checking.Id);
        await Add(TransactionType.Expense, 18413, D(9, 6), checking.Id);
        await Add(TransactionType.Expense, 10000, D(9, 7), card.Id);                    // card purchase is spending
        await Add(TransactionType.Transfer, 10000, D(9, 20), checking.Id, card.Id);    // paying the card is not
        await Add(TransactionType.Transfer, 20000, D(9, 10), checking.Id, savings.Id); // saved
        await Add(TransactionType.Transfer, 5000, D(9, 15), savings.Id, checking.Id);  // taken back out
        await Add(TransactionType.Transfer, 7000, D(9, 16), savings.Id, savings2.Id);  // savings to savings: no change
        await Add(TransactionType.Income, 99999, D(8, 31), checking.Id);               // last month
        await Add(TransactionType.Expense, 99999, D(9, 28), checking.Id);              // later this month: not yet

        var summary = await new DashboardService(db.Factory, tx).GetMonthSummaryAsync(Today);

        Assert.Equal((D(9, 1), Today), (summary.From, summary.Through));
        Assert.Equal((61240L, 28413L, 15000L), (summary.IncomeCents, summary.ExpenseCents, summary.SavedCents));
        Assert.Equal(61240 - 28413 - 15000, summary.RemainingCents);
    }

    [Fact]
    public async Task An_empty_month_is_all_zero()
    {
        await using var db = await TestDatabase.CreateAsync(Today);
        var summary = await new DashboardService(db.Factory, new TransactionService(db.Factory, db.Time)).GetMonthSummaryAsync(Today);
        Assert.Equal((0L, 0L, 0L, 0L), (summary.IncomeCents, summary.ExpenseCents, summary.SavedCents, summary.RemainingCents));
    }
}

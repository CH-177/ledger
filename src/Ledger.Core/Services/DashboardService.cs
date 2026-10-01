using Ledger.Core.Data;
using Ledger.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Core.Services;

/// <summary>This month so far, from actual transactions only.</summary>
public sealed record MonthSummary(DateOnly From, DateOnly Through, long IncomeCents, long ExpenseCents, long SavedCents)
{
    /// <summary>What's left of this month's income after spending and saving. Can be negative.</summary>
    public long RemainingCents => IncomeCents - ExpenseCents - SavedCents;
}

/// <summary>
/// The one dashboard figure no other service provides: this month's income, spending and saving.
/// Everything else on the dashboard comes straight from the account, forecast, budget and goal services.
/// </summary>
public sealed class DashboardService(IDbContextFactory<LedgerDbContext> contextFactory, TransactionService transactions)
{
    /// <summary>
    /// From the 1st of <paramref name="today"/>'s month through today. Income and spending follow the
    /// transaction rules (transfers are neither). Saved is money moved into savings accounts from other
    /// accounts, minus money moved back out; moves between two savings accounts don't count.
    /// </summary>
    public async Task<MonthSummary> GetMonthSummaryAsync(DateOnly today, CancellationToken ct = default)
    {
        var from = new DateOnly(today.Year, today.Month, 1);
        var totals = await transactions.GetTotalsAsync(new TransactionQuery(From: from, To: today), ct);

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var moves = await db.Transactions.AsNoTracking()
            .Where(t => t.TransactionType == TransactionType.Transfer && t.Date >= from && t.Date <= today)
            .Select(t => new
            {
                t.AmountCents,
                FromSavings = t.Account!.AccountType == AccountType.Savings,
                ToSavings = t.TransferAccount!.AccountType == AccountType.Savings,
            })
            .ToListAsync(ct);
        var saved = moves.Sum(m => (m.FromSavings, m.ToSavings) switch
        {
            (false, true) => m.AmountCents,
            (true, false) => -m.AmountCents,
            _ => 0L,
        });

        return new MonthSummary(from, today, totals.IncomeCents, totals.ExpenseCents, saved);
    }
}

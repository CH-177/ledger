namespace Ledger.Core.Services;

/// <summary>One month's actual income and spending. The current month runs through today only.</summary>
public sealed record MonthFlow(int Year, int Month, long IncomeCents, long ExpenseCents, bool IsPartial)
{
    public long NetCents => IncomeCents - ExpenseCents;
}

/// <summary>Net worth at the end of a month (or today, for the current month).</summary>
public sealed record NetWorthPoint(DateOnly AsOf, long AssetsCents, long LiabilitiesCents)
{
    public long NetWorthCents => AssetsCents - LiabilitiesCents;
}

/// <summary>
/// Simple reports over actual history. Built entirely from the transaction and account services,
/// so a report can never disagree with the pages it summarises.
/// </summary>
public sealed class ReportService(TransactionService transactions, AccountService accounts)
{
    /// <summary>Income and spending for the <paramref name="months"/> months ending with today's, oldest first.</summary>
    public async Task<IReadOnlyList<MonthFlow>> GetCashFlowAsync(DateOnly today, int months, CancellationToken ct = default)
    {
        var result = new List<MonthFlow>();
        var thisMonth = new DateOnly(today.Year, today.Month, 1);
        for (var i = months - 1; i >= 0; i--)
        {
            var from = thisMonth.AddMonths(-i);
            var to = i == 0 ? today : from.AddMonths(1).AddDays(-1);
            var totals = await transactions.GetTotalsAsync(new TransactionQuery(From: from, To: to), ct);
            result.Add(new MonthFlow(from.Year, from.Month, totals.IncomeCents, totals.ExpenseCents, i == 0));
        }
        return result;
    }

    /// <summary>Net worth at each month end, and today for the current month, oldest first.</summary>
    public async Task<IReadOnlyList<NetWorthPoint>> GetNetWorthHistoryAsync(DateOnly today, int months, CancellationToken ct = default)
    {
        var result = new List<NetWorthPoint>();
        var thisMonth = new DateOnly(today.Year, today.Month, 1);
        for (var i = months - 1; i >= 0; i--)
        {
            var asOf = i == 0 ? today : thisMonth.AddMonths(-i + 1).AddDays(-1);
            var worth = await accounts.GetNetWorthAsync(asOf, ct);
            result.Add(new NetWorthPoint(asOf, worth.AssetsCents, worth.LiabilitiesCents));
        }
        return result;
    }
}

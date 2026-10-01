using Ledger.Core.Forecasting;
using Ledger.Core.Data;
using Ledger.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Core.Services;

public enum ForecastEventKind
{
    /// <summary>A bill, subscription, recurring income or transfer from a recurring rule.</summary>
    Recurring,

    /// <summary>A paycheck predicted from shifts on the Income page.</summary>
    Paycheck,

    /// <summary>A transaction already recorded with a future date. Real, but not in today's balance.</summary>
    Scheduled,

    /// <summary>Something assumed by a what-if scenario. Never real.</summary>
    Scenario,
}

/// <summary>
/// One expected movement of money in a forecast. Never money that exists: it becomes real only
/// when a transaction is recorded.
/// </summary>
public sealed record ForecastEvent(
    DateOnly Date,
    string Name,
    long SignedCents,
    ForecastEventKind Kind,
    bool IsEstimated,
    long BalanceAfterCents,
    int? RecurringId = null,
    DateOnly? ScheduledDate = null)
{
    /// <summary>
    /// A past occurrence that was never recorded. It's placed on today, because the money has
    /// probably moved (or is about to) but today's balance doesn't include it yet.
    /// </summary>
    public bool IsOverdue => ScheduledDate is { } d && d < Date;
}

/// <summary>
/// A projected balance: the actual balance today, then each expected event in date order with
/// the balance it would leave. The starting balance is the only real money here.
/// </summary>
public sealed record Forecast(
    DateOnly Today,
    DateOnly Through,
    IReadOnlyList<Account> Accounts,
    long StartingBalanceCents,
    IReadOnlyList<ForecastEvent> Events)
{
    public long EndingBalanceCents => Events.Count == 0 ? StartingBalanceCents : Events[^1].BalanceAfterCents;
    public long ExpectedInCents => Events.Where(e => e.SignedCents > 0).Sum(e => e.SignedCents);
    public long ExpectedOutCents => -Events.Where(e => e.SignedCents < 0).Sum(e => e.SignedCents);

    /// <summary>The lowest projected balance and the first date it's reached (today if nothing goes lower).</summary>
    public (long Cents, DateOnly Date) Lowest =>
        Events.Where(e => e.BalanceAfterCents < StartingBalanceCents)
              .OrderBy(e => e.BalanceAfterCents).ThenBy(e => e.Date)
              .Select(e => (e.BalanceAfterCents, e.Date))
              .DefaultIfEmpty((StartingBalanceCents, Today))
              .First();
}

/// <summary>
/// Projects balances forward. It combines the actual balance today with recurring rules,
/// future-dated transactions and predicted paychecks. Pure prediction: nothing is saved.
/// </summary>
/// <remarks>
/// Designed to take goals and planned savings as further event sources later (brief §12).
/// </remarks>
public sealed class ForecastService(
    IDbContextFactory<LedgerDbContext> contextFactory,
    AccountService accounts,
    RecurringService recurring,
    IncomeForecastService paychecks)
{
    /// <summary>
    /// A past paycheck stays expected until it is recorded, however long the app was closed. This is
    /// only a cap on how far back that is worked out (a year).
    /// </summary>
    public const int PaycheckLookBackDays = 366;

    /// <summary>
    /// The forecast from <paramref name="today"/> through <paramref name="days"/> days ahead.
    /// With no <paramref name="accountId"/>, it covers every open account except credit cards
    /// (money you have, not money you owe). Moves between two covered accounts cancel out and
    /// are left out.
    /// </summary>
    public Task<Forecast> GetForecastAsync(DateOnly today, int days, int? accountId = null, CancellationToken ct = default) =>
        GetForecastAsync(today, days, accountId, null, ct);

    /// <summary>
    /// The forecast with a what-if applied: extra one-off or recurring events, stopped bills, and
    /// paychecks re-priced for changed hours or pay. Scenario events are marked <see cref="ForecastEventKind.Scenario"/>.
    /// Nothing is saved.
    /// </summary>
    public async Task<Forecast> GetForecastAsync(DateOnly today, int days, int? accountId, Scenario? scenario, CancellationToken ct = default)
    {
        var through = today.AddDays(days);
        var balances = await accounts.GetAccountBalancesAsync(includeArchived: true, asOf: today, ct: ct);
        var covered = balances
            .Where(b => accountId is { } id ? b.Account.Id == id : !b.Account.IsArchived && !b.Account.IsLiability)
            .ToList();
        var ids = covered.Select(b => b.Account.Id).ToHashSet();

        var events = new List<(DateOnly Date, string Name, long Signed, ForecastEventKind Kind, bool Estimated, int? RuleId, DateOnly? Scheduled)>();

        // Recurring rules. Missed-but-unrecorded occurrences land on today.
        foreach (var o in await recurring.GetOccurrencesAsync(today.AddDays(-RecurringService.NotRecordedLookBackDays), through, ct))
        {
            if (o.State is not (OccurrenceState.Upcoming or OccurrenceState.NotRecorded))
            {
                continue;
            }
            if (scenario?.Changes.Any(c => c.Kind == ScenarioChangeKind.StopRecurring && c.RecurringId == o.Rule.Id && o.Date >= c.From) == true)
            {
                continue;
            }
            var signed = SignedFor(o.Type, o.AmountCents, o.Rule.AccountId, o.Rule.TransferAccountId, ids);
            if (signed != 0)
            {
                events.Add((o.Date < today ? today : o.Date, o.Name, signed, ForecastEventKind.Recurring, o.Rule.IsAmountEstimated, o.Rule.Id, o.Date));
            }
        }

        // Transactions already recorded with a future date.
        await using (var db = await contextFactory.CreateDbContextAsync(ct))
        {
            var future = await db.Transactions.AsNoTracking()
                .Where(t => t.Date > today && t.Date <= through)
                .Select(t => new { t.Date, t.Payee, t.TransactionType, t.AmountCents, t.AccountId, t.TransferAccountId })
                .ToListAsync(ct);
            foreach (var t in future)
            {
                var signed = SignedFor(t.TransactionType, t.AmountCents, t.AccountId, t.TransferAccountId, ids);
                if (signed != 0)
                {
                    var name = t.Payee ?? (t.TransactionType switch
                    {
                        TransactionType.Income => "Income",
                        TransactionType.Refund => "Refund",
                        TransactionType.OutsideIn or TransactionType.OutsideOut => "Transfer (outside)",
                        TransactionType.Expense => "Spending",
                        _ => "Transfer",
                    });
                    events.Add((t.Date, name, signed, ForecastEventKind.Scheduled, false, null, null));
                }
            }

            // Predicted paychecks, until the real paycheck is recorded. A job without a deposit
            // account counts toward the combined forecast only.
            var deposits = await db.Jobs.AsNoTracking().ToDictionaryAsync(j => j.Id, j => j.DepositAccountId, ct);
            foreach (var p in await paychecks.GetPaychecksAsync(today.AddDays(-PaycheckLookBackDays), through, scenario, ct))
            {
                if (p.Recorded is not null || p.NetCents <= 0)
                {
                    continue;
                }
                var deposit = deposits.GetValueOrDefault(p.JobId);
                var counts = deposit is { } d ? ids.Contains(d) : accountId is null;
                if (counts)
                {
                    var date = p.Period.PayDate < today ? today : p.Period.PayDate;
                    events.Add((date, $"{p.JobName} paycheck", p.NetCents, ForecastEventKind.Paycheck, true, null, p.Period.PayDate));
                }
            }
        }

        // What-if events: one-offs and new recurring items. (Stopped bills and paycheck changes are applied above.)
        foreach (var c in scenario?.Changes ?? [])
        {
            var name = string.IsNullOrWhiteSpace(c.Name) ? null : c.Name.Trim();
            switch (c.Kind)
            {
                case ScenarioChangeKind.Purchase or ScenarioChangeKind.ExtraIncome when c.AmountCents > 0 && c.AccountId is { } account:
                    var date = c.From < today ? today : c.From;
                    var type = c.Kind == ScenarioChangeKind.Purchase ? TransactionType.Expense : TransactionType.Income;
                    var signed = SignedFor(type, c.AmountCents, account, null, ids);
                    if (signed != 0 && date <= through)
                    {
                        events.Add((date, name ?? (c.Kind == ScenarioChangeKind.Purchase ? "Purchase" : "Extra income"), signed, ForecastEventKind.Scenario, false, null, null));
                    }
                    break;
                case ScenarioChangeKind.AddBill or ScenarioChangeKind.AddIncome when c.AmountCents > 0 && c.AccountId is { } account:
                    var kind = c.Kind == ScenarioChangeKind.AddBill ? TransactionType.Expense : TransactionType.Income;
                    var each = SignedFor(kind, c.AmountCents, account, null, ids);
                    if (each == 0) break;
                    foreach (var d in RecurrenceSchedule.Between(c.From, null, c.Frequency, today, through))
                    {
                        events.Add((d, name ?? (kind == TransactionType.Expense ? "New bill" : "New income"), each, ForecastEventKind.Scenario, false, null, d));
                    }
                    break;
            }
        }

        // Money out before money in on the same day, so the lowest point is never understated.
        var start = covered.Sum(b => b.BalanceCents);
        var running = start;
        var ordered = new List<ForecastEvent>();
        foreach (var e in events.OrderBy(e => e.Date).ThenBy(e => e.Signed).ThenBy(e => e.Name, StringComparer.CurrentCulture))
        {
            running = checked(running + e.Signed);
            ordered.Add(new ForecastEvent(e.Date, e.Name, e.Signed, e.Kind, e.Estimated, running, e.RuleId, e.Scheduled));
        }

        return new Forecast(today, through, covered.Select(b => b.Account).ToList(), start, ordered);
    }

    /// <summary>The effect on the covered accounts: in is positive, out is negative, moves within them are zero.</summary>
    internal static long SignedFor(TransactionType type, long amount, int accountId, int? transferAccountId, IReadOnlySet<int> covered)
    {
        var from = covered.Contains(accountId);
        return type switch
        {
            TransactionType.Income or TransactionType.Refund or TransactionType.OutsideIn => from ? amount : 0,
            TransactionType.Expense or TransactionType.OutsideOut => from ? -amount : 0,
            _ => (from, transferAccountId is { } to && covered.Contains(to)) switch
            {
                (true, false) => -amount,
                (false, true) => amount,
                _ => 0,
            },
        };
    }
}

using Ledger.Core.Forecasting;
using Ledger.Core.Models;
using Ledger.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Core.Tests;

public class GoalMathTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    [Theory]
    [InlineData(120000, 82000, 68)]   // the brief's MacBook: $820 / $1,200
    [InlineData(100000, 99999, 99)]   // never rounds up to 100% before it's reached
    [InlineData(100000, 100000, 100)]
    [InlineData(100000, 250000, 100)] // over-saved is still 100%
    [InlineData(100000, 0, 0)]
    public void Percent_rounds_down_and_caps(long target, long current, int expected) =>
        Assert.Equal(expected, GoalMath.Percent(target, current));

    [Fact]
    public void Remaining_is_never_negative()
    {
        Assert.Equal(38000, GoalMath.Remaining(120000, 82000));
        Assert.Equal(0, GoalMath.Remaining(120000, 130000));
    }

    [Theory]
    [InlineData("2026-09-27", "2027-06-15", 9)]  // Sep 27, Oct 27 … May 27
    [InlineData("2026-09-27", "2027-06-27", 10)] // … and Jun 27 itself
    [InlineData("2026-09-27", "2026-09-27", 1)]  // due today: one chance, today
    [InlineData("2026-09-27", "2026-10-26", 1)]
    [InlineData("2026-01-31", "2026-02-28", 2)]  // Jan 31, then Feb 28 (short month)
    [InlineData("2026-09-27", "2026-09-26", 0)]  // already passed
    public void Months_left_counts_monthly_contributions_starting_today(string today, string target, int expected) =>
        Assert.Equal(expected, GoalMath.MonthsLeft(DateOnly.Parse(today), DateOnly.Parse(target)));

    [Fact]
    public void Suggested_monthly_rounds_up_so_following_it_is_never_short()
    {
        // $380 left over 9 months = $42.222… → $42.23; 9 × $42.23 = $380.07 ≥ $380.
        Assert.Equal(4223, GoalMath.SuggestedMonthly(120000, 82000, D(2026, 9, 27), D(2027, 6, 15)));
        Assert.Equal(1000, GoalMath.SuggestedMonthly(12000, 0, D(2026, 1, 1), D(2026, 12, 1))); // exact
    }

    [Fact]
    public void No_suggestion_without_a_date_after_the_date_or_once_reached()
    {
        Assert.Null(GoalMath.SuggestedMonthly(120000, 82000, D(2026, 9, 27), null));
        Assert.Null(GoalMath.SuggestedMonthly(120000, 82000, D(2026, 9, 27), D(2026, 9, 1)));
        Assert.Null(GoalMath.SuggestedMonthly(120000, 120000, D(2026, 9, 27), D(2027, 1, 1)));
    }
}

public class GoalServiceTests
{
    private static readonly DateOnly Today = new(2026, 9, 27);

    private sealed class Fixture : IAsyncDisposable
    {
        public required TestDatabase Db { get; init; }
        public required GoalService Goals { get; init; }
        public required Account Checking { get; init; }
        public required Account Savings { get; init; }
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private static async Task<Fixture> CreateAsync()
    {
        var db = await TestDatabase.CreateAsync(Today);
        return new Fixture
        {
            Db = db,
            Goals = new GoalService(db.Factory, new AccountService(db.Factory, db.Time), db.Time),
            Checking = await db.AddAccountAsync("Checking", AccountType.Checking, 42000),
            Savings = await db.AddAccountAsync("Savings", AccountType.Savings, 56621),
        };
    }

    private static GoalInput MacBook => new("MacBook", 120000, new DateOnly(2027, 6, 15), GoalTracking.Manual, 82000);

    [Fact]
    public async Task A_manual_goal_is_its_starting_amount_plus_contributions()
    {
        await using var f = await CreateAsync();
        var goal = await f.Goals.CreateAsync(MacBook);
        var progress = await f.Goals.GetAsync(goal.Id);
        Assert.Equal((82000L, 38000L, 68, 4223L), (progress.CurrentCents, progress.RemainingCents, progress.Percent, progress.SuggestedMonthlyCents));

        await f.Goals.AddContributionAsync(goal.Id, 10000, Today, " Birthday money ");
        await f.Goals.AddContributionAsync(goal.Id, -2500, Today);
        Assert.Equal(89500, (await f.Goals.GetAsync(goal.Id)).CurrentCents);

        var history = await f.Goals.GetContributionsAsync(goal.Id);
        Assert.Equal([-2500L, 10000L], history.Select(c => c.AmountCents));
        Assert.Equal("Birthday money", history[1].Note);
    }

    [Fact]
    public async Task Contributions_never_change_account_balances()
    {
        await using var f = await CreateAsync();
        var goal = await f.Goals.CreateAsync(MacBook with { AccountId = f.Savings.Id });
        await f.Goals.AddContributionAsync(goal.Id, 10000, Today);
        var accounts = new AccountService(f.Db.Factory, f.Db.Time);
        Assert.Equal(56621, await accounts.GetBalanceAsync(f.Savings.Id));
        await using var db = f.Db.Context();
        Assert.Equal(0, await db.Transactions.CountAsync());
    }

    [Fact]
    public async Task Taking_out_more_than_is_saved_is_refused()
    {
        await using var f = await CreateAsync();
        var goal = await f.Goals.CreateAsync(MacBook with { StartingAmountCents = 5000 });
        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Goals.AddContributionAsync(goal.Id, -5001, Today));
        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Goals.AddContributionAsync(goal.Id, 0, Today));
        await f.Goals.AddContributionAsync(goal.Id, -5000, Today);
        Assert.Equal(0, (await f.Goals.GetAsync(goal.Id)).CurrentCents);
    }

    [Fact]
    public async Task Removing_a_contribution_undoes_it_but_never_below_zero()
    {
        await using var f = await CreateAsync();
        var goal = await f.Goals.CreateAsync(MacBook with { StartingAmountCents = 0 });
        var added = await f.Goals.AddContributionAsync(goal.Id, 10000, Today);
        await f.Goals.AddContributionAsync(goal.Id, -8000, Today);
        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Goals.DeleteContributionAsync(added.Id)); // would be -$80
        var taken = (await f.Goals.GetContributionsAsync(goal.Id)).First(c => c.AmountCents < 0);
        await f.Goals.DeleteContributionAsync(taken.Id);
        await f.Goals.DeleteContributionAsync(added.Id);
        Assert.Equal(0, (await f.Goals.GetAsync(goal.Id)).CurrentCents);
    }

    [Fact]
    public async Task An_account_backed_goal_follows_the_actual_balance()
    {
        await using var f = await CreateAsync();
        var goal = await f.Goals.CreateAsync(new GoalInput("Emergency fund", 100000, null, GoalTracking.AccountBalance, 99999, f.Savings.Id));
        var progress = await f.Goals.GetAsync(goal.Id);
        Assert.Equal((56621L, 0L, 56), (progress.CurrentCents, progress.Goal.StartingAmountCents, progress.Percent)); // starting amount ignored
        Assert.Null(progress.SuggestedMonthlyCents); // no date, no suggestion

        await new TransactionService(f.Db.Factory, f.Db.Time).CreateAsync(
            new TransactionInput(TransactionType.Transfer, 10000, Today, f.Checking.Id, f.Savings.Id));
        Assert.Equal(66621, (await f.Goals.GetAsync(goal.Id)).CurrentCents);

        // Contributions don't apply: move money into the account instead.
        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Goals.AddContributionAsync(goal.Id, 100, Today));
    }

    [Fact]
    public async Task Each_account_backs_at_most_one_goal_and_never_a_credit_card()
    {
        await using var f = await CreateAsync();
        var backed = new GoalInput("Emergency fund", 100000, null, GoalTracking.AccountBalance, 0, f.Savings.Id);
        var first = await f.Goals.CreateAsync(backed);
        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Goals.CreateAsync(backed with { Name = "Car" }));
        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Goals.CreateAsync(backed with { Name = "Car", AccountId = null }));

        var card = await f.Db.AddAccountAsync("Card", AccountType.CreditCard);
        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Goals.CreateAsync(backed with { Name = "Car", AccountId = card.Id }));

        // Archiving frees the account; restoring while another goal uses it is refused.
        await f.Goals.SetArchivedAsync(first.Id, true);
        await f.Goals.CreateAsync(backed with { Name = "Car" });
        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Goals.SetArchivedAsync(first.Id, false));
        // A manual goal may still say where its money is kept.
        await f.Goals.CreateAsync(MacBook with { AccountId = f.Savings.Id });
    }

    [Fact]
    public async Task Rejects_invalid_goals()
    {
        await using var f = await CreateAsync();
        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Goals.CreateAsync(MacBook with { Name = " " }));
        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Goals.CreateAsync(MacBook with { TargetAmountCents = 0 }));
        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Goals.CreateAsync(MacBook with { StartingAmountCents = -1 }));
        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Goals.CreateAsync(MacBook with { TargetAmountCents = Money.MaxAmountCents + 1 }));
        await f.Goals.CreateAsync(MacBook);
        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Goals.CreateAsync(MacBook with { Name = "macbook" }));
    }

    [Fact]
    public async Task Archived_goals_are_hidden_and_deleting_removes_contributions()
    {
        await using var f = await CreateAsync();
        var goal = await f.Goals.CreateAsync(MacBook);
        await f.Goals.AddContributionAsync(goal.Id, 1000, Today);
        await f.Goals.SetArchivedAsync(goal.Id, true);
        Assert.Empty(await f.Goals.GetGoalsAsync());
        Assert.Single(await f.Goals.GetGoalsAsync(includeArchived: true));

        await f.Goals.DeleteAsync(goal.Id);
        await using var db = f.Db.Context();
        Assert.False(await db.GoalContributions.AnyAsync());
    }

    [Fact]
    public async Task Reached_and_past_due_are_reported()
    {
        await using var f = await CreateAsync();
        var reached = await f.Goals.CreateAsync(MacBook with { Name = "Done", StartingAmountCents = 120000 });
        var late = await f.Goals.CreateAsync(MacBook with { Name = "Late", TargetDate = new DateOnly(2026, 9, 1) });
        Assert.Equal((true, false), ((await f.Goals.GetAsync(reached.Id)).IsReached, (await f.Goals.GetAsync(reached.Id)).IsPastDue));
        var lateProgress = await f.Goals.GetAsync(late.Id);
        Assert.Equal((false, true, (long?)null), (lateProgress.IsReached, lateProgress.IsPastDue, lateProgress.SuggestedMonthlyCents));
    }
}

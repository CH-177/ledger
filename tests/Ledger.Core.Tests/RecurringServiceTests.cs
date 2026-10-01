using Ledger.Core.Models;
using Ledger.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Core.Tests;

public class RecurringServiceTests
{
    private static readonly DateOnly Today = new(2026, 9, 26);
    private static DateOnly D(int m, int d) => new(2026, m, d);

    private sealed class Fixture : IAsyncDisposable
    {
        public required TestDatabase Db { get; init; }
        public required RecurringService Recurring { get; init; }
        public required TransactionService Transactions { get; init; }
        public required Account Checking { get; init; }
        public required Account Savings { get; init; }
        public ValueTask DisposeAsync() => Db.DisposeAsync();

        public async Task<int> PayAsync(long cents, DateOnly date, TransactionType type = TransactionType.Expense) =>
            (await Transactions.CreateAsync(new TransactionInput(type, cents, date, Checking.Id))).Id;
    }

    private static async Task<Fixture> CreateAsync()
    {
        var db = await TestDatabase.CreateAsync(Today);
        return new Fixture
        {
            Db = db,
            Recurring = new RecurringService(db.Factory, db.Time),
            Transactions = new TransactionService(db.Factory, db.Time),
            Checking = await db.AddAccountAsync("Checking", AccountType.Checking, 50000),
            Savings = await db.AddAccountAsync("Savings", AccountType.Savings, 100000),
        };
    }

    private static RecurringInput Insurance(int accountId, DateOnly? start = null) =>
        new("Car insurance", TransactionType.Expense, 20000, RecurrenceFrequency.Monthly, start ?? D(9, 30), accountId);

    [Fact]
    public async Task Creates_and_updates_a_rule()
    {
        await using var f = await CreateAsync();
        var rule = await f.Recurring.CreateAsync(Insurance(f.Checking.Id) with { Name = "  Car insurance  ", Memo = " " });
        Assert.Equal(("Car insurance", null, true), (rule.Name, rule.Memo, rule.IsActive));

        await f.Recurring.UpdateAsync(rule.Id, Insurance(f.Checking.Id) with { AmountCents = 21000, Frequency = RecurrenceFrequency.Quarterly });
        var updated = await f.Recurring.GetAsync(rule.Id);
        Assert.Equal((21000L, RecurrenceFrequency.Quarterly), (updated.AmountCents, updated.Frequency));
    }

    [Fact]
    public async Task Rejects_invalid_rules()
    {
        await using var f = await CreateAsync();
        var ok = Insurance(f.Checking.Id);
        var income = await CategoryAsync(f, CategoryType.Income);

        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Recurring.CreateAsync(ok with { Name = " " }));
        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Recurring.CreateAsync(ok with { Name = new string('x', 101) }));
        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Recurring.CreateAsync(ok with { AmountCents = 0 }));
        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Recurring.CreateAsync(ok with { EndDate = D(9, 29) }));
        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Recurring.CreateAsync(ok with { Frequency = (RecurrenceFrequency)9 }));
        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Recurring.CreateAsync(ok with { CategoryId = income }));
        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Recurring.CreateAsync(ok with { Type = TransactionType.Transfer }));
        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Recurring.CreateAsync(ok with { Type = TransactionType.Transfer, TransferAccountId = f.Checking.Id }));
        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Recurring.CreateAsync(ok with { AccountId = 999 }));

        var transfer = await f.Recurring.CreateAsync(ok with { Name = "To savings", Type = TransactionType.Transfer, TransferAccountId = f.Savings.Id });
        Assert.Equal(f.Savings.Id, transfer.TransferAccountId);
        var notTransfer = await f.Recurring.CreateAsync(ok with { TransferAccountId = f.Savings.Id });
        Assert.Null(notTransfer.TransferAccountId); // ignored unless it's a transfer
    }

    [Fact]
    public async Task Upcoming_occurrences_are_forecasts_and_nothing_is_created()
    {
        await using var f = await CreateAsync();
        await f.Recurring.CreateAsync(Insurance(f.Checking.Id));

        var occurrences = await f.Recurring.GetOccurrencesAsync(Today, D(12, 31));
        Assert.Equal([D(9, 30), D(10, 30), D(11, 30), D(12, 30)], occurrences.Select(o => o.Date));
        Assert.All(occurrences, o => Assert.Equal(OccurrenceState.Upcoming, o.State));

        await using var db = f.Db.Context();
        Assert.Equal(0, await db.Transactions.CountAsync());
    }

    [Fact]
    public async Task Missed_occurrences_stay_until_handled_however_long_the_app_was_closed_but_never_before_the_rule_was_added()
    {
        await using var f = await CreateAsync();
        f.Db.Time.Today = D(7, 1);
        var rule = await f.Recurring.CreateAsync(Insurance(f.Checking.Id, D(6, 15)) with { Frequency = RecurrenceFrequency.Weekly });
        f.Db.Time.Today = Today; // nearly three months later

        var past = await f.Recurring.GetOccurrencesAsync(Today.AddDays(-RecurringService.NotRecordedLookBackDays), Today.AddDays(-1));
        // Weekly from Jun 15, added Jul 1: Jul 6 through Sep 21, nothing dropped for being old.
        Assert.Equal(12, past.Count);
        Assert.Equal((D(7, 6), D(9, 21)), (past[0].Date, past[^1].Date));
        Assert.All(past, o => Assert.Equal(OccurrenceState.NotRecorded, o.State));

        // "Skip all" clears the backlog in one go.
        Assert.Equal(12, await f.Recurring.SkipManyAsync(past.Select(o => (rule.Id, o.Date))));
        Assert.All(await f.Recurring.GetOccurrencesAsync(D(7, 1), Today.AddDays(-1)), o => Assert.Equal(OccurrenceState.Skipped, o.State));

        // A rule added today never reminds about dates before it existed.
        var fresh = await f.Recurring.CreateAsync(Insurance(f.Checking.Id, D(9, 1)) with { Name = "Phone" });
        Assert.DoesNotContain(await f.Recurring.GetOccurrencesAsync(D(9, 1), Today.AddDays(-1)), o => o.Rule.Id == fresh.Id);
    }

    [Fact]
    public async Task Recording_links_a_transaction_once_and_a_deleted_one_frees_the_date()
    {
        await using var f = await CreateAsync();
        var rule = await f.Recurring.CreateAsync(Insurance(f.Checking.Id));
        var paid = await f.PayAsync(20412, D(9, 29)); // paid a day early, slightly different amount

        await f.Recurring.RecordAsync(rule.Id, D(9, 30), paid);
        var first = (await f.Recurring.GetOccurrencesAsync(Today, D(10, 31)))[0];
        Assert.Equal((OccurrenceState.Recorded, paid, 20412L), (first.State, first.TransactionId, first.RecordedCents));

        // Recording the same payment again is harmless; a second payment for the same date is refused.
        await f.Recurring.RecordAsync(rule.Id, D(9, 30), paid);
        var second = await f.PayAsync(20000, D(9, 30));
        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Recurring.RecordAsync(rule.Id, D(9, 30), second));
        // …and one transaction can't pay two dates.
        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Recurring.RecordAsync(rule.Id, D(10, 30), paid));
        // Only scheduled dates can be recorded.
        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Recurring.RecordAsync(rule.Id, D(10, 1), second));

        // Deleting the recorded transaction puts the occurrence back, and it can be recorded again.
        await f.Transactions.DeleteAsync(paid);
        Assert.Equal(OccurrenceState.Upcoming, (await f.Recurring.GetOccurrencesAsync(Today, D(9, 30)))[0].State);
        await f.Recurring.RecordAsync(rule.Id, D(9, 30), second);
        Assert.Equal(second, (await f.Recurring.GetOccurrencesAsync(Today, D(9, 30)))[0].TransactionId);

        // Undoing the delete brings the old transaction back without a clash.
        await f.Transactions.RestoreAsync(paid);
        Assert.Null((await f.Transactions.GetAsync(paid)).RecurringTransactionId);
    }

    [Fact]
    public async Task Skipping_removes_an_occurrence_until_undone_and_recording_clears_a_skip()
    {
        await using var f = await CreateAsync();
        var rule = await f.Recurring.CreateAsync(Insurance(f.Checking.Id));

        await f.Recurring.SkipAsync(rule.Id, D(10, 30));
        await f.Recurring.SkipAsync(rule.Id, D(10, 30)); // twice is harmless
        Assert.Equal([OccurrenceState.Upcoming, OccurrenceState.Skipped, OccurrenceState.Upcoming],
            (await f.Recurring.GetOccurrencesAsync(Today, D(11, 30))).Select(o => o.State));
        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Recurring.SkipAsync(rule.Id, D(10, 29)));

        await f.Recurring.UnskipAsync(rule.Id, D(10, 30));
        Assert.Equal(OccurrenceState.Upcoming, (await f.Recurring.GetOccurrencesAsync(D(10, 30), D(10, 30)))[0].State);

        await f.Recurring.SkipAsync(rule.Id, D(10, 30));
        await f.Recurring.RecordAsync(rule.Id, D(10, 30), await f.PayAsync(20000, D(10, 30)));
        Assert.Equal(OccurrenceState.Recorded, (await f.Recurring.GetOccurrencesAsync(D(10, 30), D(10, 30)))[0].State);
        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Recurring.SkipAsync(rule.Id, D(10, 30)));
    }

    [Fact]
    public async Task Pausing_stops_future_occurrences_but_keeps_history()
    {
        await using var f = await CreateAsync();
        var rule = await f.Recurring.CreateAsync(Insurance(f.Checking.Id));
        await f.Recurring.RecordAsync(rule.Id, D(9, 30), await f.PayAsync(20000, D(9, 30)));

        await f.Recurring.SetActiveAsync(rule.Id, false);
        var occurrences = await f.Recurring.GetOccurrencesAsync(Today, D(12, 31));
        Assert.Equal(OccurrenceState.Recorded, Assert.Single(occurrences).State);
        Assert.Null(RecurringService.NextDate((await f.Recurring.GetAsync(rule.Id)), occurrences, Today));

        await f.Recurring.SetActiveAsync(rule.Id, true);
        var resumed = await f.Recurring.GetOccurrencesAsync(Today, D(12, 31));
        Assert.Equal(D(10, 30), RecurringService.NextDate(await f.Recurring.GetAsync(rule.Id), resumed, Today));
    }

    [Fact]
    public async Task Only_a_rule_with_no_recorded_payments_can_be_deleted()
    {
        await using var f = await CreateAsync();
        var used = await f.Recurring.CreateAsync(Insurance(f.Checking.Id));
        var paid = await f.PayAsync(20000, D(9, 30));
        await f.Recurring.RecordAsync(used.Id, D(9, 30), paid);
        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Recurring.DeleteAsync(used.Id));
        await f.Transactions.DeleteAsync(paid); // still history, even when deleted
        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Recurring.DeleteAsync(used.Id));

        var unused = await f.Recurring.CreateAsync(Insurance(f.Checking.Id) with { Name = "Spotify" });
        await f.Recurring.SkipAsync(unused.Id, D(10, 30));
        await f.Recurring.DeleteAsync(unused.Id);
        await using var db = f.Db.Context();
        Assert.False(await db.RecurringTransactions.AnyAsync(r => r.Id == unused.Id));
        Assert.False(await db.RecurringSkips.AnyAsync());
    }

    // ---- Recording automatically ----------------------------------------------------------------

    [Fact]
    public async Task Auto_record_files_each_due_date_since_it_was_turned_on_exactly_once()
    {
        await using var f = await CreateAsync();
        // Spotify monthly from Jul 1; auto-record turned on Aug 20, then the app sits closed.
        f.Db.Time.Today = D(8, 20);
        var spotify = await f.Recurring.CreateAsync(new RecurringInput("Spotify", TransactionType.Expense, 1199, RecurrenceFrequency.Monthly,
            D(7, 1), f.Checking.Id, AutoRecord: true));
        Assert.Equal(D(8, 20), spotify.AutoRecordSince);

        f.Db.Time.Today = D(11, 5); // opened again after ~2.5 months
        var result = await f.Recurring.AutoRecordDueAsync();
        // Sep 1, Oct 1, Nov 1. Not Jul 1 or Aug 1: those were before auto-record was turned on.
        Assert.Equal([D(9, 1), D(10, 1), D(11, 1)], result.Recorded.Select(r => r.Date));
        Assert.Empty(result.Failed);

        var txs = await f.Transactions.QueryAsync(new TransactionQuery());
        Assert.Equal(3, txs.Count);
        Assert.All(txs, t => Assert.True(t.IsAutoRecorded && t.Payee == "Spotify" && t.AmountCents == 1199 && t.Type == TransactionType.Expense));
        Assert.All(await f.Recurring.GetOccurrencesAsync(D(9, 1), D(11, 1)), o => Assert.Equal(OccurrenceState.Recorded, o.State));

        // Running again (every launch) never duplicates.
        Assert.True((await f.Recurring.AutoRecordDueAsync()).IsEmpty);
        Assert.Equal(3, (await f.Transactions.QueryAsync(new TransactionQuery())).Count);

        // The balance reflects them: $500 − 3 × $11.99.
        Assert.Equal(50000 - 3 * 1199, await new AccountService(f.Db.Factory, f.Db.Time).GetBalanceAsync(f.Checking.Id));
    }

    [Fact]
    public async Task Deleting_an_automatic_entry_sends_that_date_back_to_needs_recording_instead_of_recreating_it()
    {
        await using var f = await CreateAsync();
        var rule = await f.Recurring.CreateAsync(Insurance(f.Checking.Id, Today) with { AutoRecord = true });
        var first = Assert.Single((await f.Recurring.AutoRecordDueAsync()).Recorded);

        await f.Transactions.DeleteAsync(first.TransactionId);
        Assert.True((await f.Recurring.AutoRecordDueAsync()).IsEmpty);
        f.Db.Time.Today = Today.AddDays(1);
        Assert.Equal(OccurrenceState.NotRecorded, (await f.Recurring.GetOccurrencesAsync(Today, Today)).Single(o => o.Rule.Id == rule.Id).State);
    }

    [Fact]
    public async Task Skipped_dates_paused_rules_and_future_dates_are_not_auto_recorded()
    {
        await using var f = await CreateAsync();
        var skipped = await f.Recurring.CreateAsync(Insurance(f.Checking.Id, Today) with { AutoRecord = true });
        await f.Recurring.SkipAsync(skipped.Id, Today);
        var paused = await f.Recurring.CreateAsync(Insurance(f.Checking.Id, Today) with { Name = "Gym", AutoRecord = true });
        await f.Recurring.SetActiveAsync(paused.Id, false);
        await f.Recurring.CreateAsync(Insurance(f.Checking.Id, Today.AddDays(1)) with { Name = "Phone", AutoRecord = true });

        Assert.True((await f.Recurring.AutoRecordDueAsync()).IsEmpty);
    }

    [Fact]
    public async Task Turning_auto_record_off_and_on_restarts_from_that_day()
    {
        await using var f = await CreateAsync();
        var rule = await f.Recurring.CreateAsync(Insurance(f.Checking.Id, D(9, 1)) with { AutoRecord = true });
        Assert.Equal(Today, rule.AutoRecordSince);

        f.Db.Time.Today = D(10, 15);
        await f.Recurring.UpdateAsync(rule.Id, Insurance(f.Checking.Id, D(9, 1)) with { AutoRecord = true, AmountCents = 21000 });
        Assert.Equal(Today, (await f.Recurring.GetAsync(rule.Id)).AutoRecordSince); // staying on keeps its start

        await f.Recurring.UpdateAsync(rule.Id, Insurance(f.Checking.Id, D(9, 1)));
        Assert.Null((await f.Recurring.GetAsync(rule.Id)).AutoRecordSince);
        await f.Recurring.UpdateAsync(rule.Id, Insurance(f.Checking.Id, D(9, 1)) with { AutoRecord = true });
        Assert.Equal(D(10, 15), (await f.Recurring.GetAsync(rule.Id)).AutoRecordSince);
        Assert.True((await f.Recurring.AutoRecordDueAsync()).IsEmpty); // Oct 1 predates turning it back on
    }

    [Fact]
    public async Task Estimated_amounts_cannot_be_auto_recorded()
    {
        await using var f = await CreateAsync();
        await Assert.ThrowsAsync<LedgerValidationException>(() =>
            f.Recurring.CreateAsync(Insurance(f.Checking.Id) with { IsAmountEstimated = true, AutoRecord = true }));
    }

    [Fact]
    public async Task A_date_that_cannot_be_recorded_is_reported_and_left_for_the_user()
    {
        await using var f = await CreateAsync();
        var rule = await f.Recurring.CreateAsync(Insurance(f.Checking.Id, Today) with { AutoRecord = true });
        await new AccountService(f.Db.Factory, f.Db.Time).SetArchivedAsync(f.Checking.Id, true);

        var result = await f.Recurring.AutoRecordDueAsync();
        var failure = Assert.Single(result.Failed);
        Assert.Equal(("Car insurance", Today), (failure.Name, failure.Date));
        Assert.Contains("archived", failure.Reason);
        Assert.Empty(result.Recorded);
        _ = rule;
    }

    private static async Task<int> CategoryAsync(Fixture f, CategoryType type)
    {
        await using var db = f.Db.Context();
        return await db.Categories.Where(c => c.CategoryType == type).Select(c => c.Id).FirstAsync();
    }
}

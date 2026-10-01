using Ledger.Core.Models;
using Ledger.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Core.Tests;

public class SuggestionTests
{
    private static readonly DateOnly Today = new(2026, 9, 30);
    private static DateOnly D(int m, int d) => new(2026, m, d);

    private sealed class Fixture : IAsyncDisposable
    {
        public required TestDatabase Db { get; init; }
        public required SuggestionService Suggestions { get; init; }
        public required TransactionService Transactions { get; init; }
        public required Account Checking { get; init; }
        public required Account Card { get; init; }
        public required int Groceries { get; init; }
        public required int Dining { get; init; }
        public ValueTask DisposeAsync() => Db.DisposeAsync();

        public Task Spend(long cents, DateOnly date, string payee, int? category = null, Account? account = null, string? memo = null) =>
            Transactions.CreateAsync(new TransactionInput(TransactionType.Expense, cents, date, (account ?? Checking).Id, CategoryId: category, Payee: payee, Memo: memo));
    }

    private static async Task<Fixture> CreateAsync()
    {
        var db = await TestDatabase.CreateAsync(Today);
        await using var ctx = db.Context();
        var expense = await ctx.Categories.Where(c => c.CategoryType == CategoryType.Expense && c.ParentCategoryId != null).OrderBy(c => c.Id).ToListAsync();
        return new Fixture
        {
            Db = db,
            Suggestions = new SuggestionService(db.Factory, new SettingsService(db.Factory)),
            Transactions = new TransactionService(db.Factory, db.Time),
            Checking = await db.AddAccountAsync("Checking", AccountType.Checking, 100000),
            Card = await db.AddAccountAsync("Card", AccountType.CreditCard),
            Groceries = expense.First(c => c.Name == "Groceries").Id,
            Dining = expense.First(c => c.Name != "Groceries").Id,
        };
    }

    [Fact]
    public async Task Habits_come_from_recent_entries_in_any_letter_case_and_a_one_off_teaches_nothing()
    {
        await using var f = await CreateAsync();
        await f.Spend(842, D(9, 1), "Quickmart", f.Groceries);
        Assert.Null(await f.Suggestions.GetHabitsAsync("quickmart", TransactionType.Expense)); // one entry: nothing yet

        await f.Spend(1000, D(9, 8), "QUICKMART", f.Dining, f.Card);
        await f.Spend(1200, D(9, 15), "quickmart", f.Dining, f.Card);
        await f.Spend(4000, D(9, 22), "Quickmart", f.Dining, f.Card); // an unusual fill-up
        var habits = await f.Suggestions.GetHabitsAsync(" Quickmart ", TransactionType.Expense);

        // The category changed: the most used recently wins. The range is the middle of the amounts.
        Assert.Equal(("Quickmart", 4, (int?)f.Dining, (int?)f.Card.Id), (habits!.Payee, habits.Entries, habits.CategoryId, habits.AccountId));
        Assert.Equal((1100L, 842L, 4000L), (habits.TypicalCents, habits.LowCents, habits.HighCents));
        // A refund learns from spending there.
        Assert.Equal((int?)f.Dining, (await f.Suggestions.GetHabitsAsync("Quickmart", TransactionType.Refund))!.CategoryId);
        Assert.Null(await f.Suggestions.GetHabitsAsync("Quickmart", TransactionType.Income));
    }

    [Fact]
    public async Task Notes_are_suggested_from_any_payee_most_used_first()
    {
        await using var f = await CreateAsync();
        await f.Spend(100, D(9, 1), "Quickmart", memo: "gas");
        await f.Spend(100, D(9, 2), "Valumart", memo: "lunch w/ Sam");
        await f.Spend(100, D(9, 3), "Wawa", memo: "Lunch w/ Sam");
        await f.Spend(100, D(9, 4), "Wawa", memo: "lunch at work");
        Assert.Equal(["lunch at work", "Lunch w/ Sam"], (await f.Suggestions.GetNoteSuggestionsAsync("LUNCH")).Order());
        Assert.Equal("Lunch w/ Sam", (await f.Suggestions.GetNoteSuggestionsAsync("lun"))[0]); // used twice
        Assert.Empty(await f.Suggestions.GetNoteSuggestionsAsync(" "));
    }

    [Fact]
    public async Task A_monthly_payment_is_spotted_with_its_usual_day_and_amount()
    {
        await using var f = await CreateAsync();
        foreach (var (m, d) in new[] { (6, 21), (7, 21), (8, 22), (9, 20) })
        {
            await f.Spend(1377, D(m, d), m == 7 ? "SPOTIFY" : "Spotify", f.Dining);
        }
        var repeat = Assert.Single(await f.Suggestions.FindRepeatsAsync(Today));
        Assert.Equal((RecurrenceFrequency.Monthly, 1377L, false, 4, D(10, 21), (int?)f.Dining),
            (repeat.Frequency, repeat.TypicalCents, repeat.AmountVaries, repeat.Occurrences, repeat.NextDate, repeat.CategoryId));
    }

    [Fact]
    public async Task Weekly_and_every_other_week_are_spotted_and_month_ends_count_as_the_same_day()
    {
        await using var f = await CreateAsync();
        foreach (var d in new[] { D(9, 4), D(9, 11), D(9, 18), D(9, 25) }) await f.Spend(500, d, "Laundromat");
        foreach (var d in new[] { D(8, 21), D(9, 4), D(9, 18) }) await f.Spend(2500, d, "Gym", f.Dining, f.Card);
        foreach (var d in new[] { D(6, 30), D(7, 31), D(8, 31), D(9, 30) }) await f.Spend(4000, d, "Rent share");
        var found = (await f.Suggestions.FindRepeatsAsync(Today)).ToDictionary(r => r.Payee);
        Assert.Equal(RecurrenceFrequency.Weekly, found["Laundromat"].Frequency);
        Assert.Equal((RecurrenceFrequency.Biweekly, f.Card.Id, D(10, 2)), (found["Gym"].Frequency, found["Gym"].AccountId, found["Gym"].NextDate));
        Assert.Equal((RecurrenceFrequency.Monthly, D(10, 30)), (found["Rent share"].Frequency, found["Rent share"].NextDate));
    }

    [Fact]
    public async Task Irregular_spacing_changing_amounts_stopped_payments_and_bills_are_not_suggested()
    {
        await using var f = await CreateAsync();
        foreach (var d in new[] { D(7, 2), D(7, 20), D(9, 1) }) await f.Spend(1000, d, "Irregular");            // no rhythm
        foreach (var (d, c) in new[] { (D(7, 5), 1000L), (D(8, 5), 2000L), (D(9, 5), 1000L) }) await f.Spend(c, d, "Wobbly"); // amounts vary too much
        foreach (var d in new[] { D(4, 10), D(5, 10), D(6, 10) }) await f.Spend(999, d, "Cancelled");          // stopped in June
        foreach (var d in new[] { D(7, 15), D(8, 15), D(9, 15) }) await f.Spend(2999, d, "Netflix.com");       // already a bill
        var recurring = new RecurringService(f.Db.Factory, f.Db.Time);
        await recurring.CreateAsync(new RecurringInput("Netflix", TransactionType.Expense, 2999, RecurrenceFrequency.Monthly, D(10, 15), f.Checking.Id));
        Assert.Empty(await f.Suggestions.FindRepeatsAsync(Today));
    }

    [Fact]
    public async Task A_dismissed_repeat_stays_dismissed_and_spotting_can_be_turned_off()
    {
        await using var f = await CreateAsync();
        foreach (var d in new[] { D(7, 21), D(8, 21), D(9, 21) }) await f.Spend(1377, d, "Spotify");
        foreach (var d in new[] { D(7, 3), D(8, 3), D(9, 3) }) await f.Spend(1099, d, "Phone");
        var both = await f.Suggestions.FindRepeatsAsync(Today);
        Assert.Equal(2, both.Count);

        await f.Suggestions.DismissRepeatAsync(both.Single(r => r.Payee == "Spotify").Key);
        Assert.Equal("Phone", Assert.Single(await f.Suggestions.FindRepeatsAsync(Today)).Payee);

        await f.Suggestions.SetSpottingRepeatsAsync(false);
        Assert.Empty(await f.Suggestions.FindRepeatsAsync(Today));
        await f.Suggestions.SetSpottingRepeatsAsync(true);
        Assert.Single(await f.Suggestions.FindRepeatsAsync(Today));
    }

    [Fact]
    public async Task Payee_suggestions_list_the_most_used_first_with_their_usual_category()
    {
        await using var f = await CreateAsync();
        await f.Spend(100, D(9, 1), "Valumart Markets", f.Groceries);
        await f.Spend(100, D(9, 2), "Valumart Markets", f.Groceries);
        await f.Spend(100, D(9, 3), "Valumart Markets", f.Dining);
        await f.Spend(100, D(9, 20), "Valumartino's Deli", f.Dining);
        var list = await f.Transactions.GetPayeeSuggestionsAsync("valumart", TransactionType.Expense);
        Assert.Equal([("Valumart Markets", (int?)f.Groceries), ("Valumartino's Deli", (int?)f.Dining)], list.Select(s => (s.Payee, s.CategoryId)));
    }
}

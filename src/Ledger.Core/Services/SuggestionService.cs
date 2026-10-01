using Ledger.Core.Data;
using Ledger.Core.Forecasting;
using Ledger.Core.Import;
using Ledger.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Core.Services;

/// <summary>
/// What you usually do with a payee, learned from your recent entries: the category and account you
/// use most, and the typical amount (median) with its usual range. Only for payees used at least
/// <see cref="SuggestionService.MinEntries"/> times; a one-off teaches nothing.
/// </summary>
public sealed record PayeeHabits(string Payee, int Entries, int? CategoryId, int? AccountId, long TypicalCents, long LowCents, long HighCents)
{
    public bool AmountVaries => LowCents != HighCents;
}

/// <summary>
/// Something you've entered on a regular schedule that isn't a bill yet ("Spotify, $11.99, monthly
/// around the 21st, 4 times"). <see cref="Key"/> identifies it for "Don't suggest this".
/// </summary>
public sealed record RepeatSuggestion(
    string Key,
    string Payee,
    TransactionType Type,
    int AccountId,
    int? CategoryId,
    RecurrenceFrequency Frequency,
    long TypicalCents,
    bool AmountVaries,
    int Occurrences,
    DateOnly FirstDate,
    DateOnly LastDate,
    DateOnly NextDate);

/// <summary>
/// Autofill: suggestions learned from your own history, computed locally and never saved on their
/// own. Payee habits and past notes help the transaction editor; repeating entries are offered as
/// bills. Thresholds are in docs/RULES.md ("Suggestions").
/// </summary>
public sealed class SuggestionService(IDbContextFactory<LedgerDbContext> contextFactory, SettingsService settings)
{
    /// <summary>A payee needs this many entries before Ledger suggests anything about it.</summary>
    public const int MinEntries = 2;

    /// <summary>How many of a payee's latest entries its habits are learned from (recent beats old).</summary>
    public const int RecentEntries = 12;

    /// <summary>A repeat needs this many entries on a regular schedule.</summary>
    public const int MinRepeats = 3;

    /// <summary>Each amount in a repeat is within this share of the typical amount.</summary>
    public const double AmountTolerance = 0.20;

    /// <summary>Monthly repeats land within this many days of their usual day of the month.</summary>
    public const int DayTolerance = 3;

    /// <summary>How far back repeats are looked for.</summary>
    public const int RepeatLookBackDays = 400;

    // ---- Payee habits -----------------------------------------------------------------------------

    /// <summary>
    /// Habits for <paramref name="payee"/> (any letter case) as the given kind of transaction. A refund
    /// learns from spending at that payee. Null when there's nothing to learn yet.
    /// </summary>
    public async Task<PayeeHabits?> GetHabitsAsync(string payee, TransactionType type, CancellationToken ct = default)
    {
        var name = payee.Trim();
        if (name.Length == 0 || type is not (TransactionType.Expense or TransactionType.Income or TransactionType.Refund))
        {
            return null;
        }
        var historyType = type == TransactionType.Refund ? TransactionType.Expense : type;
        var lower = name.ToLowerInvariant();
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var recent = await db.Transactions.AsNoTracking()
            .Where(t => t.TransactionType == historyType && t.Payee != null && t.Payee.Trim().ToLower() == lower)
            .OrderByDescending(t => t.Date).ThenByDescending(t => t.Id)
            .Select(t => new { t.Payee, t.CategoryId, t.AccountId, t.AmountCents })
            .Take(RecentEntries)
            .ToListAsync(ct);
        if (recent.Count < MinEntries)
        {
            return null;
        }

        // The most used category and account among recent entries; a tie goes to the latest one used.
        static int? Usual(IEnumerable<int?> values) => values
            .Select((v, i) => (v, i))
            .Where(x => x.v is not null)
            .GroupBy(x => x.v)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Min(x => x.i))
            .Select(g => g.Key)
            .FirstOrDefault();

        var amounts = recent.Select(r => r.AmountCents).Order().ToList();
        var trim = (amounts.Count - 1) / 4; // the middle half: one unusual entry doesn't widen the range
        return new PayeeHabits(
            recent[0].Payee!.Trim(),
            recent.Count,
            Usual(recent.Select(r => r.CategoryId)),
            Usual(recent.Select(r => (int?)r.AccountId)),
            Median(amounts),
            amounts[trim],
            amounts[^(trim + 1)]);
    }

    // ---- Notes ------------------------------------------------------------------------------------

    /// <summary>
    /// Notes you've written before that contain <paramref name="text"/>, from any payee: the most used
    /// first, then the most recent. Offered as a pick list, never filled in.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetNoteSuggestionsAsync(string text, int take = 8, CancellationToken ct = default)
    {
        var part = text.Trim();
        if (part.Length == 0)
        {
            return [];
        }
        var lower = part.ToLowerInvariant();
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var memos = await db.Transactions.AsNoTracking()
            .Where(t => t.Memo != null && t.Memo.ToLower().Contains(lower))
            .OrderByDescending(t => t.Date).ThenByDescending(t => t.Id)
            .Select(t => new { t.Memo, t.Date })
            .Take(500)
            .ToListAsync(ct);
        return memos
            .GroupBy(m => m.Memo!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Key.Length > 0 && !string.Equals(g.Key, part, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Max(m => m.Date))
            .Select(g => g.First().Memo!.Trim())
            .Take(take)
            .ToList();
    }

    // ---- Repeats ------------------------------------------------------------------------------------

    public async Task<bool> IsSpottingRepeatsAsync(CancellationToken ct = default) =>
        await settings.GetAsync(SettingKeys.SpotRepeats, ct) != "false";

    public Task SetSpottingRepeatsAsync(bool on, CancellationToken ct = default) =>
        settings.SetAsync(SettingKeys.SpotRepeats, on ? "true" : "false", ct);

    /// <summary>"Don't suggest this": the repeat is never offered again.</summary>
    public async Task DismissRepeatAsync(string key, CancellationToken ct = default)
    {
        var dismissed = await DismissedAsync(ct);
        if (dismissed.Add(key))
        {
            await settings.SetAsync(SettingKeys.DismissedRepeats, string.Join('\n', dismissed.Order(StringComparer.Ordinal)), ct);
        }
    }

    /// <summary>
    /// Entries of the same kind, payee (any letter case) and account that repeat weekly, every two
    /// weeks or monthly, at least <see cref="MinRepeats"/> times in a row up to recently, with amounts
    /// within <see cref="AmountTolerance"/> of their median. Entries already recorded for a bill, payees
    /// a bill already covers, and dismissed repeats are left out.
    /// </summary>
    public async Task<IReadOnlyList<RepeatSuggestion>> FindRepeatsAsync(DateOnly today, CancellationToken ct = default)
    {
        if (!await IsSpottingRepeatsAsync(ct))
        {
            return [];
        }
        var dismissed = await DismissedAsync(ct);
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var from = today.AddDays(-RepeatLookBackDays);
        var entries = await db.Transactions.AsNoTracking()
            .Where(t => t.Date >= from && t.Date <= today && t.Payee != null && t.RecurringTransactionId == null
                        && (t.TransactionType == TransactionType.Expense || t.TransactionType == TransactionType.Income))
            .Select(t => new { t.Payee, t.TransactionType, t.AccountId, t.CategoryId, t.Date, t.AmountCents })
            .ToListAsync(ct);
        var rules = await db.RecurringTransactions.AsNoTracking().Where(r => r.IsActive).ToListAsync(ct);

        var found = new List<RepeatSuggestion>();
        foreach (var group in entries.GroupBy(e => (Payee: e.Payee!.Trim().ToLowerInvariant(), e.TransactionType, e.AccountId)))
        {
            var key = $"{(int)group.Key.TransactionType}|{group.Key.AccountId}|{group.Key.Payee}";
            if (group.Count() < MinRepeats || dismissed.Contains(key))
            {
                continue;
            }
            var list = group.OrderBy(e => e.Date).ToList();
            var payee = list[^1].Payee!.Trim();
            if (rules.Any(r => r.TransactionType == group.Key.TransactionType && r.AccountId == group.Key.AccountId
                               && (ImportService.SimilarPayee(r.Name, payee) || r.Payee is { } p && ImportService.SimilarPayee(p, payee))))
            {
                continue;
            }

            // The longest run up to the latest entry that keeps one rhythm and one amount.
            foreach (var frequency in new[] { RecurrenceFrequency.Monthly, RecurrenceFrequency.Biweekly, RecurrenceFrequency.Weekly })
            {
                RepeatSuggestion? match = null;
                for (var take = list.Count; take >= MinRepeats && match is null; take--)
                {
                    var run = list.Skip(list.Count - take).ToList();
                    var dates = run.Select(e => e.Date).ToList();
                    var median = Median(run.Select(e => e.AmountCents).Order().ToList());
                    if (!KeepsRhythm(dates, frequency) || run.Any(e => Math.Abs(e.AmountCents - median) > median * AmountTolerance))
                    {
                        continue;
                    }
                    var next = NextDate(dates, frequency);
                    if (next < today.AddDays(-DayTolerance))
                    {
                        break; // it stopped: the next one is already overdue
                    }
                    match = new RepeatSuggestion(key, payee, group.Key.TransactionType, group.Key.AccountId,
                        run.Where(e => e.CategoryId is not null).GroupBy(e => e.CategoryId).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault(),
                        frequency, median, run.Select(e => e.AmountCents).Distinct().Count() > 1, run.Count, dates[0], dates[^1], next);
                }
                if (match is not null)
                {
                    found.Add(match);
                    break;
                }
            }
        }
        return found.OrderBy(r => r.NextDate).ThenBy(r => r.Payee, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Whether consecutive dates keep the frequency: 7 or 14 days apart (±1 or ±2), or a month apart landing near the same day.</summary>
    internal static bool KeepsRhythm(IReadOnlyList<DateOnly> dates, RecurrenceFrequency frequency)
    {
        for (var i = 1; i < dates.Count; i++)
        {
            var gap = dates[i].DayNumber - dates[i - 1].DayNumber;
            var ok = frequency switch
            {
                RecurrenceFrequency.Weekly => gap is >= 6 and <= 8,
                RecurrenceFrequency.Biweekly => gap is >= 12 and <= 16,
                RecurrenceFrequency.Monthly => gap is >= 25 and <= 36,
                _ => false,
            };
            if (!ok) return false;
        }
        if (frequency == RecurrenceFrequency.Monthly)
        {
            var usual = UsualDay(dates);
            return dates.All(d => DayDistance(d, usual) <= DayTolerance);
        }
        return true;
    }

    internal static DateOnly NextDate(IReadOnlyList<DateOnly> dates, RecurrenceFrequency frequency)
    {
        var last = dates[^1];
        if (frequency != RecurrenceFrequency.Monthly)
        {
            return last.AddDays(frequency == RecurrenceFrequency.Weekly ? 7 : 14);
        }
        var month = last.AddMonths(1);
        return new DateOnly(month.Year, month.Month, Math.Min(UsualDay(dates), DateTime.DaysInMonth(month.Year, month.Month)));
    }

    /// <summary>The usual day of the month (median), so one early or late entry doesn't move it.</summary>
    private static int UsualDay(IReadOnlyList<DateOnly> dates) => (int)Median(dates.Select(d => (long)d.Day).Order().ToList());

    /// <summary>Days between a date and the usual day in its month (the 31st and the 30th of a 30-day month are 0 apart).</summary>
    private static int DayDistance(DateOnly date, int usualDay) =>
        Math.Abs(date.Day - Math.Min(usualDay, DateTime.DaysInMonth(date.Year, date.Month)));

    private static long Median(IReadOnlyList<long> sorted) =>
        sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;

    private async Task<HashSet<string>> DismissedAsync(CancellationToken ct) =>
        (await settings.GetAsync(SettingKeys.DismissedRepeats, ct) ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
}

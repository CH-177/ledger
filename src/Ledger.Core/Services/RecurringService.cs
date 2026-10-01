using Ledger.Core.Data;
using Ledger.Core.Forecasting;
using Ledger.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Core.Services;

/// <summary>Fields the user supplies for a bill, subscription, recurring income or recurring transfer.</summary>
public sealed record RecurringInput(
    string Name,
    TransactionType Type,
    long AmountCents,
    RecurrenceFrequency Frequency,
    DateOnly StartDate,
    int AccountId,
    DateOnly? EndDate = null,
    int? TransferAccountId = null,
    int? CategoryId = null,
    bool IsAmountEstimated = false,
    string? Memo = null,
    bool AutoRecord = false);

public enum OccurrenceState
{
    /// <summary>Today or later, not recorded yet. A forecast.</summary>
    Upcoming,

    /// <summary>Its date has passed and nothing was recorded for it.</summary>
    NotRecorded,

    /// <summary>A real transaction was recorded for it.</summary>
    Recorded,

    /// <summary>The user said it won't happen.</summary>
    Skipped,
}

/// <summary>One scheduled date of a recurring rule, and what has happened to it.</summary>
public sealed record RecurringOccurrence(
    RecurringTransaction Rule,
    DateOnly Date,
    OccurrenceState State,
    int? TransactionId = null,
    long? RecordedCents = null)
{
    public string Name => Rule.Name;
    public long AmountCents => Rule.AmountCents;
    public TransactionType Type => Rule.TransactionType;
}

/// <summary>A transaction Ledger recorded automatically.</summary>
public sealed record AutoRecordedItem(string Name, DateOnly Date, long AmountCents, TransactionType Type, int TransactionId);

/// <summary>A due date that couldn't be recorded automatically (for example, its account was archived).</summary>
public sealed record AutoRecordFailure(string Name, DateOnly Date, string Reason);

public sealed record AutoRecordResult(IReadOnlyList<AutoRecordedItem> Recorded, IReadOnlyList<AutoRecordFailure> Failed)
{
    public static readonly AutoRecordResult None = new([], []);
    public bool IsEmpty => Recorded.Count == 0 && Failed.Count == 0;
}

/// <summary>
/// Manages recurring rules and their occurrences. Occurrences are computed, not stored. A rule
/// only creates transactions when the user set it to record automatically (and then at most once
/// per date); otherwise the user records an occurrence (which links a real transaction to that
/// date, at most once) or skips it.
/// </summary>
public sealed class RecurringService(IDbContextFactory<LedgerDbContext> contextFactory, TimeProvider time)
{
    /// <summary>
    /// A missed occurrence asks to be recorded until it is recorded or skipped, however long the app
    /// was closed. This is only a cap on how far back that is worked out (a year).
    /// </summary>
    public const int NotRecordedLookBackDays = 366;

    private DateOnly Today => DateOnly.FromDateTime(time.GetLocalNow().Date);

    /// <summary>Every rule with its account and category, active ones first, then by name.</summary>
    public async Task<IReadOnlyList<RecurringTransaction>> GetRulesAsync(CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.RecurringTransactions.AsNoTracking()
            .Include(r => r.Account)
            .Include(r => r.TransferAccount)
            .Include(r => r.Category).ThenInclude(c => c!.ParentCategory)
            .OrderByDescending(r => r.IsActive).ThenBy(r => r.Name)
            .ToListAsync(ct);
    }

    public async Task<RecurringTransaction> GetAsync(int id, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.RecurringTransactions.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new LedgerValidationException("That recurring item no longer exists.");
    }

    public async Task<RecurringTransaction> CreateAsync(RecurringInput input, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await ValidateAsync(db, input, existing: null, ct);
        var rule = new RecurringTransaction { Name = "" };
        Apply(rule, input, Today);
        db.RecurringTransactions.Add(rule);
        await db.SaveChangesAsync(ct);
        return rule;
    }

    /// <summary>
    /// Changes a rule. Occurrences already recorded keep their transactions and dates; only the
    /// schedule from now on follows the new settings.
    /// </summary>
    public async Task UpdateAsync(int id, RecurringInput input, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var rule = await db.RecurringTransactions.FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new LedgerValidationException("That recurring item no longer exists.");
        await ValidateAsync(db, input, rule, ct);
        Apply(rule, input, Today);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Pausing stops future occurrences and reminders; nothing already recorded changes.</summary>
    public async Task SetActiveAsync(int id, bool active, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var rule = await db.RecurringTransactions.FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new LedgerValidationException("That recurring item no longer exists.");
        rule.IsActive = active;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Deletes a rule that has never been recorded. A rule with recorded transactions is part of
    /// their history, so it can only be paused.
    /// </summary>
    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var rule = await db.RecurringTransactions.FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new LedgerValidationException("That recurring item no longer exists.");
        if (await db.Transactions.IgnoreQueryFilters().AnyAsync(t => t.RecurringTransactionId == id, ct))
        {
            throw new LedgerValidationException($"“{rule.Name}” has recorded payments, so it can't be deleted. Pause it instead.");
        }
        db.RecurringTransactions.Remove(rule);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Every occurrence of active rules between <paramref name="from"/> and <paramref name="to"/>,
    /// by date. Past occurrences are <see cref="OccurrenceState.NotRecorded"/> until handled (within
    /// <see cref="NotRecordedLookBackDays"/>), and never before the rule was added. Recorded and skipped occurrences are included for inactive rules too.
    /// </summary>
    public async Task<IReadOnlyList<RecurringOccurrence>> GetOccurrencesAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var today = Today;
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var rules = await db.RecurringTransactions.AsNoTracking()
            .Include(r => r.Account)
            .Include(r => r.TransferAccount)
            .ToListAsync(ct);
        var recorded = await db.Transactions.AsNoTracking()
            .Where(t => t.RecurringTransactionId != null && t.RecurrenceDate >= from && t.RecurrenceDate <= to)
            .Select(t => new { RuleId = t.RecurringTransactionId!.Value, Date = t.RecurrenceDate!.Value, t.Id, t.AmountCents })
            .ToListAsync(ct);
        var skips = await db.RecurringSkips.AsNoTracking()
            .Where(s => s.Date >= from && s.Date <= to)
            .Select(s => new { s.RecurringTransactionId, s.Date })
            .ToListAsync(ct);

        var result = new List<RecurringOccurrence>();
        foreach (var rule in rules)
        {
            var added = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(rule.CreatedAt, DateTimeKind.Utc), time.LocalTimeZone));
            var remindFrom = new[] { today.AddDays(-NotRecordedLookBackDays), added }.Max();

            foreach (var date in RecurrenceSchedule.Between(rule.StartDate, rule.EndDate, rule.Frequency, from, to))
            {
                if (recorded.FirstOrDefault(r => r.RuleId == rule.Id && r.Date == date) is { } r)
                {
                    result.Add(new RecurringOccurrence(rule, date, OccurrenceState.Recorded, r.Id, r.AmountCents));
                }
                else if (skips.Any(s => s.RecurringTransactionId == rule.Id && s.Date == date))
                {
                    result.Add(new RecurringOccurrence(rule, date, OccurrenceState.Skipped));
                }
                else if (!rule.IsActive)
                {
                    continue;
                }
                else if (date >= today)
                {
                    result.Add(new RecurringOccurrence(rule, date, OccurrenceState.Upcoming));
                }
                else if (date >= remindFrom)
                {
                    result.Add(new RecurringOccurrence(rule, date, OccurrenceState.NotRecorded));
                }
            }
        }
        return result.OrderBy(o => o.Date).ThenBy(o => o.Name).ToList();
    }

    /// <summary>The next date on or after <paramref name="onOrAfter"/> that is neither recorded nor skipped.</summary>
    public static DateOnly? NextDate(RecurringTransaction rule, IEnumerable<RecurringOccurrence> occurrences, DateOnly onOrAfter)
    {
        if (!rule.IsActive)
        {
            return null;
        }
        var handled = occurrences.Where(o => o.Rule.Id == rule.Id && o.State is OccurrenceState.Recorded or OccurrenceState.Skipped)
            .Select(o => o.Date).ToHashSet();
        return RecurrenceSchedule.Between(rule.StartDate, rule.EndDate, rule.Frequency, onOrAfter, DateOnly.MaxValue.AddYears(-1))
            .Take(500)
            .Cast<DateOnly?>()
            .FirstOrDefault(d => !handled.Contains(d!.Value));
    }

    /// <summary>
    /// Links a transaction the user just recorded to the occurrence it pays. Each occurrence can be
    /// recorded once; recording it removes any skip. A deleted transaction no longer holds the slot.
    /// </summary>
    public async Task RecordAsync(int ruleId, DateOnly date, int transactionId, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var rule = await db.RecurringTransactions.AsNoTracking().FirstOrDefaultAsync(r => r.Id == ruleId, ct)
            ?? throw new LedgerValidationException("That recurring item no longer exists.");
        if (!RecurrenceSchedule.IsOccurrence(rule.StartDate, rule.EndDate, rule.Frequency, date))
        {
            throw new LedgerValidationException($"“{rule.Name}” isn't scheduled on {date:MMM d}.");
        }

        var transaction = await db.Transactions.FirstOrDefaultAsync(t => t.Id == transactionId, ct)
            ?? throw new LedgerValidationException("That transaction no longer exists.");
        if (transaction.RecurringTransactionId is not null && (transaction.RecurringTransactionId != ruleId || transaction.RecurrenceDate != date))
        {
            throw new LedgerValidationException("That transaction is already recorded for another scheduled payment.");
        }

        var holders = await db.Transactions.IgnoreQueryFilters()
            .Where(t => t.RecurringTransactionId == ruleId && t.RecurrenceDate == date && t.Id != transactionId)
            .ToListAsync(ct);
        if (holders.Any(t => !t.IsDeleted))
        {
            throw new LedgerValidationException($"“{rule.Name}” on {date:MMM d} is already recorded.");
        }
        foreach (var deleted in holders)
        {
            deleted.RecurringTransactionId = null;
            deleted.RecurrenceDate = null;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct); // release the slot before claiming it (unique index)
        transaction.RecurringTransactionId = ruleId;
        transaction.RecurrenceDate = date;
        db.RecurringSkips.RemoveRange(db.RecurringSkips.Where(s => s.RecurringTransactionId == ruleId && s.Date == date));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Marks an occurrence as not happening. Skipping twice is harmless.</summary>
    public async Task SkipAsync(int ruleId, DateOnly date, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var rule = await db.RecurringTransactions.AsNoTracking().FirstOrDefaultAsync(r => r.Id == ruleId, ct)
            ?? throw new LedgerValidationException("That recurring item no longer exists.");
        if (!RecurrenceSchedule.IsOccurrence(rule.StartDate, rule.EndDate, rule.Frequency, date))
        {
            throw new LedgerValidationException($"“{rule.Name}” isn't scheduled on {date:MMM d}.");
        }
        if (await db.Transactions.AnyAsync(t => t.RecurringTransactionId == ruleId && t.RecurrenceDate == date, ct))
        {
            throw new LedgerValidationException($"“{rule.Name}” on {date:MMM d} is already recorded.");
        }
        if (!await db.RecurringSkips.AnyAsync(s => s.RecurringTransactionId == ruleId && s.Date == date, ct))
        {
            db.RecurringSkips.Add(new RecurringSkip { RecurringTransactionId = ruleId, Date = date });
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Undoes <see cref="SkipAsync"/>.</summary>
    public async Task UnskipAsync(int ruleId, DateOnly date, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await db.RecurringSkips.Where(s => s.RecurringTransactionId == ruleId && s.Date == date).ExecuteDeleteAsync(ct);
    }

    /// <summary>Skips several occurrences at once (clearing a backlog). Recorded ones are left alone.</summary>
    public async Task<int> SkipManyAsync(IEnumerable<(int RuleId, DateOnly Date)> occurrences, CancellationToken ct = default)
    {
        var skipped = 0;
        foreach (var (ruleId, date) in occurrences.Distinct())
        {
            try
            {
                await SkipAsync(ruleId, date, ct);
                skipped++;
            }
            catch (LedgerValidationException)
            {
                // Recorded or rescheduled in the meantime: nothing to skip.
            }
        }
        return skipped;
    }

    /// <summary>
    /// Records every due date of rules set to record automatically: from the day auto-record was
    /// turned on, through today. Safe to run any number of times. A date that already has a
    /// transaction (even one the user deleted), or was skipped, is never recorded again, so deleting
    /// an automatic entry sends that date back to "Needs recording" instead of recreating it.
    /// </summary>
    public async Task<AutoRecordResult> AutoRecordDueAsync(CancellationToken ct = default)
    {
        var today = Today;
        var recorded = new List<AutoRecordedItem>();
        var failed = new List<AutoRecordFailure>();

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var rules = await db.RecurringTransactions.AsNoTracking()
            .Where(r => r.IsActive && r.AutoRecordSince != null && !r.IsAmountEstimated)
            .ToListAsync(ct);

        foreach (var rule in rules)
        {
            var from = new[] { rule.AutoRecordSince!.Value, rule.StartDate, today.AddDays(-NotRecordedLookBackDays) }.Max();
            var due = RecurrenceSchedule.Between(rule.StartDate, rule.EndDate, rule.Frequency, from, today).ToList();
            if (due.Count == 0)
            {
                continue;
            }

            var handled = (await db.Transactions.IgnoreQueryFilters()
                    .Where(t => t.RecurringTransactionId == rule.Id && t.RecurrenceDate >= from && t.RecurrenceDate <= today)
                    .Select(t => t.RecurrenceDate!.Value).ToListAsync(ct))
                .Concat(await db.RecurringSkips
                    .Where(s => s.RecurringTransactionId == rule.Id && s.Date >= from && s.Date <= today)
                    .Select(s => s.Date).ToListAsync(ct))
                .ToHashSet();

            foreach (var date in due.Where(d => !handled.Contains(d)))
            {
                var input = new TransactionInput(
                    rule.TransactionType, rule.AmountCents, date, rule.AccountId, rule.TransferAccountId, rule.CategoryId, rule.Name, rule.Memo);
                try
                {
                    await TransactionService.ValidateAsync(db, input, existing: null, ct);
                }
                catch (LedgerValidationException ex)
                {
                    // Left for the user in "Needs recording", with the reason.
                    failed.Add(new AutoRecordFailure(rule.Name, date, ex.Message));
                    continue;
                }

                var transaction = new Transaction
                {
                    TransactionType = rule.TransactionType,
                    AmountCents = rule.AmountCents,
                    Date = date,
                    AccountId = rule.AccountId,
                    TransferAccountId = rule.TransactionType == TransactionType.Transfer ? rule.TransferAccountId : null,
                    CategoryId = rule.CategoryId,
                    Payee = rule.Name,
                    Memo = rule.Memo,
                    Status = TransactionStatus.Cleared,
                    RecurringTransactionId = rule.Id,
                    RecurrenceDate = date,
                    IsAutoRecorded = true,
                };
                db.Transactions.Add(transaction);
                try
                {
                    await db.SaveChangesAsync(ct);
                    recorded.Add(new AutoRecordedItem(rule.Name, date, rule.AmountCents, rule.TransactionType, transaction.Id));
                }
                catch (DbUpdateException)
                {
                    // Already recorded for this date (the unique index): nothing to do.
                    db.Entry(transaction).State = EntityState.Detached;
                }
            }
        }
        return new AutoRecordResult(recorded, failed);
    }

    private static void Apply(RecurringTransaction rule, RecurringInput input, DateOnly today)
    {
        // Turning auto-record on starts from today; keeping it on keeps its original start.
        rule.AutoRecordSince = input.AutoRecord ? rule.AutoRecordSince ?? today : null;
        rule.Name = input.Name.Trim();
        rule.TransactionType = input.Type;
        rule.AmountCents = input.AmountCents;
        rule.IsAmountEstimated = input.IsAmountEstimated;
        rule.Frequency = input.Frequency;
        rule.StartDate = input.StartDate;
        rule.EndDate = input.EndDate;
        rule.AccountId = input.AccountId;
        rule.TransferAccountId = input.Type == TransactionType.Transfer ? input.TransferAccountId : null;
        rule.CategoryId = input.CategoryId;
        rule.Memo = string.IsNullOrWhiteSpace(input.Memo) ? null : input.Memo.Trim();
    }

    private static async Task ValidateAsync(LedgerDbContext db, RecurringInput input, RecurringTransaction? existing, CancellationToken ct)
    {
        var name = input.Name?.Trim() ?? "";
        if (name.Length == 0)
        {
            throw new LedgerValidationException("Give it a name, like “Spotify” or “Car insurance”.");
        }
        if (name.Length > 100)
        {
            throw new LedgerValidationException("Names can be up to 100 characters.");
        }
        if (input.Type == TransactionType.Refund)
        {
            throw new LedgerValidationException("Refunds aren't scheduled. Record each one as it arrives.");
        }
        if (input.Type is TransactionType.OutsideIn or TransactionType.OutsideOut)
        {
            throw new LedgerValidationException("Transfers with someone else's account aren't scheduled yet. Record each one as it happens.");
        }
        if (!Enum.IsDefined(input.Frequency))
        {
            throw new LedgerValidationException("Choose how often it repeats.");
        }
        if (input.EndDate is { } end && end < input.StartDate)
        {
            throw new LedgerValidationException("The end date can't be before the first date.");
        }
        if (input.AutoRecord && input.IsAmountEstimated)
        {
            throw new LedgerValidationException("Only fixed amounts can be recorded automatically. Untick “The amount varies”, or record each one yourself.");
        }

        // The same rules as a real transaction: amount, dates, usable accounts, matching category.
        var asTransaction = new TransactionInput(
            input.Type, input.AmountCents, input.StartDate, input.AccountId, input.TransferAccountId, input.CategoryId, name, input.Memo);
        var current = existing is null ? null : new Transaction
        {
            AccountId = existing.AccountId,
            TransferAccountId = existing.TransferAccountId,
            CategoryId = existing.CategoryId,
        };
        await TransactionService.ValidateAsync(db, asTransaction, current, ct);
    }
}

using Ledger.Core.Data;
using Ledger.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Core.Services;

/// <summary>Fields the user supplies when recording or editing a transaction.</summary>
public sealed record TransactionInput(
    TransactionType Type,
    long AmountCents,
    DateOnly Date,
    int AccountId,
    int? TransferAccountId = null,
    int? CategoryId = null,
    string? Payee = null,
    string? Memo = null,
    TransactionStatus Status = TransactionStatus.Cleared,
    int? PaidFromGoalId = null);

/// <summary>Filters for the transaction list. Every filter is optional.</summary>
public sealed record TransactionQuery(
    string? Search = null,
    DateOnly? From = null,
    DateOnly? To = null,
    int? AccountId = null,
    int? CategoryId = null,
    TransactionType? Type = null);

/// <summary>A transaction with the names needed to display it.</summary>
public sealed record TransactionListItem(
    int Id,
    TransactionType Type,
    DateOnly Date,
    long AmountCents,
    string? Payee,
    string? Memo,
    TransactionStatus Status,
    int AccountId,
    string AccountName,
    int? TransferAccountId,
    string? TransferAccountName,
    int? CategoryId,
    string? CategoryName,
    string? ParentCategoryName,
    bool IsAutoRecorded = false)
{
    /// <summary>"Food › Groceries", "Entertainment", or null when uncategorised.</summary>
    public string? CategoryFullName =>
        CategoryName is null ? null : ParentCategoryName is null ? CategoryName : $"{ParentCategoryName} › {CategoryName}";

    /// <summary>
    /// The amount's effect on <paramref name="accountId"/>: positive for money in, negative for
    /// money out. For a transfer viewed without an account there is no direction, so null.
    /// </summary>
    public long? SignedAmountFor(int? accountId) => Type switch
    {
        TransactionType.Income or TransactionType.Refund or TransactionType.OutsideIn => AmountCents,
        TransactionType.Expense or TransactionType.OutsideOut => -AmountCents,
        _ when accountId == TransferAccountId => AmountCents,
        _ when accountId == AccountId => -AmountCents,
        _ => null,
    };
}

/// <summary>
/// Income and spending for a set of transactions. Transfers count toward neither. Spending is net of
/// refunds (<see cref="RefundCents"/> is shown separately), so it can be negative in a month of returns.
/// </summary>
public sealed record TransactionTotals(int Count, long IncomeCents, long ExpenseCents, long RefundCents = 0);

/// <summary>A previously used payee, with the category it was last used with.</summary>
public sealed record PayeeSuggestion(string Payee, int? CategoryId);

/// <summary>
/// Records, edits and finds actual transactions. Deleting is a soft delete that can be undone;
/// deleted transactions are excluded from every calculation.
/// </summary>
public sealed class TransactionService(IDbContextFactory<LedgerDbContext> contextFactory, TimeProvider time)
{
    private static readonly DateOnly EarliestDate = new(1900, 1, 1);
    private static readonly DateOnly LatestDate = new(2100, 12, 31);

    public async Task<Transaction> CreateAsync(TransactionInput input, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await ValidateAsync(db, input, existing: null, ct);

        var transaction = new Transaction();
        Apply(transaction, input);
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync(ct);
        return transaction;
    }

    public async Task UpdateAsync(int id, TransactionInput input, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var transaction = await db.Transactions.FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new LedgerValidationException("That transaction no longer exists.");
        await ValidateAsync(db, input, transaction, ct);
        Apply(transaction, input);
        await db.SaveChangesAsync(ct);
    }

    public async Task<Transaction> GetAsync(int id, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.Transactions.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new LedgerValidationException("That transaction no longer exists.");
    }

    /// <summary>Hides a transaction from every view and calculation. It can be restored.</summary>
    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var transaction = await db.Transactions.FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new LedgerValidationException("That transaction no longer exists.");
        transaction.IsDeleted = true;
        transaction.DeletedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Undoes <see cref="DeleteAsync"/>.</summary>
    public async Task RestoreAsync(int id, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var transaction = await db.Transactions.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new LedgerValidationException("That transaction no longer exists.");
        transaction.IsDeleted = false;
        transaction.DeletedAt = null;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Returns matching transactions, newest first.</summary>
    public async Task<IReadOnlyList<TransactionListItem>> QueryAsync(TransactionQuery query, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var filtered = await ApplyFiltersAsync(db, query, ct);
        return await filtered
            .OrderByDescending(t => t.Date).ThenByDescending(t => t.Id)
            .Select(t => new TransactionListItem(
                t.Id, t.TransactionType, t.Date, t.AmountCents, t.Payee, t.Memo, t.Status,
                t.AccountId, t.Account!.Name,
                t.TransferAccountId, t.TransferAccount != null ? t.TransferAccount.Name : null,
                t.CategoryId, t.Category != null ? t.Category.Name : null,
                t.Category != null && t.Category.ParentCategory != null ? t.Category.ParentCategory.Name : null,
                t.IsAutoRecorded))
            .ToListAsync(ct);
    }

    /// <summary>Income and spending across the transactions matching <paramref name="query"/>.</summary>
    public async Task<TransactionTotals> GetTotalsAsync(TransactionQuery query, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var filtered = await ApplyFiltersAsync(db, query, ct);
        var rows = await filtered
            .GroupBy(t => t.TransactionType)
            .Select(g => new { Type = g.Key, Count = g.Count(), Sum = g.Sum(t => t.AmountCents) })
            .ToListAsync(ct);
        return new TransactionTotals(
            rows.Sum(r => r.Count),
            rows.Where(r => r.Type == TransactionType.Income).Sum(r => r.Sum),
            rows.Where(r => r.Type == TransactionType.Expense).Sum(r => r.Sum) - rows.Where(r => r.Type == TransactionType.Refund).Sum(r => r.Sum),
            rows.Where(r => r.Type == TransactionType.Refund).Sum(r => r.Sum));
    }

    /// <summary>
    /// Past payees containing <paramref name="text"/>: the most used first, then the most recent, each
    /// with the category it's usually filed under.
    /// </summary>
    public async Task<IReadOnlyList<PayeeSuggestion>> GetPayeeSuggestionsAsync(
        string text, TransactionType type, int take = 8, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var pattern = LikeContains(text.Trim());
        // A refund comes from somewhere you've spent money, so it learns from spending.
        var historyType = type == TransactionType.Refund ? TransactionType.Expense : type;
        var recent = await db.Transactions.AsNoTracking()
            .Where(t => t.TransactionType == historyType && t.Payee != null && EF.Functions.Like(t.Payee, pattern, EscapeChar))
            .OrderByDescending(t => t.Date).ThenByDescending(t => t.Id)
            .Select(t => new { t.Payee, t.CategoryId })
            .Take(500)
            .ToListAsync(ct);

        return recent
            .Select((r, i) => (r.Payee, r.CategoryId, Order: i))
            .GroupBy(r => r.Payee!.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Min(r => r.Order))
            .Select(g => new PayeeSuggestion(g.First().Payee!.Trim(),
                g.Where(r => r.CategoryId is not null).GroupBy(r => r.CategoryId).OrderByDescending(c => c.Count()).ThenBy(c => c.Min(r => r.Order))
                    .Select(c => c.Key).FirstOrDefault()))
            .Take(take)
            .ToList();
    }

    private async Task<IQueryable<Transaction>> ApplyFiltersAsync(LedgerDbContext db, TransactionQuery query, CancellationToken ct)
    {
        var q = db.Transactions.AsNoTracking();

        if (query.From is { } from) q = q.Where(t => t.Date >= from);
        if (query.To is { } to) q = q.Where(t => t.Date <= to);
        if (query.Type is TransactionType.Transfer)
        {
            // "Transfers" includes transfers with someone else's account.
            q = q.Where(t => t.TransactionType == TransactionType.Transfer || t.TransactionType == TransactionType.OutsideIn || t.TransactionType == TransactionType.OutsideOut);
        }
        else if (query.Type is { } type)
        {
            q = q.Where(t => t.TransactionType == type);
        }
        if (query.AccountId is { } accountId)
        {
            q = q.Where(t => t.AccountId == accountId || t.TransferAccountId == accountId);
        }
        if (query.CategoryId is { } categoryId)
        {
            // A group matches its own transactions and those of its subcategories.
            var ids = await db.Categories.Where(c => c.Id == categoryId || c.ParentCategoryId == categoryId)
                .Select(c => c.Id).ToListAsync(ct);
            q = q.Where(t => t.CategoryId != null && ids.Contains(t.CategoryId.Value));
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var text = query.Search.Trim();
            var pattern = LikeContains(text);
            var isAmount = Money.TryParse(text, out var cents) && cents > 0;
            q = q.Where(t =>
                (t.Payee != null && EF.Functions.Like(t.Payee, pattern, EscapeChar)) ||
                (t.Memo != null && EF.Functions.Like(t.Memo, pattern, EscapeChar)) ||
                (t.Category != null && EF.Functions.Like(t.Category.Name, pattern, EscapeChar)) ||
                (t.Category != null && t.Category.ParentCategory != null && EF.Functions.Like(t.Category.ParentCategory.Name, pattern, EscapeChar)) ||
                (isAmount && t.AmountCents == cents));
        }
        return q;
    }

    private const string EscapeChar = "\\";

    /// <summary>A LIKE pattern matching <paramref name="text"/> literally anywhere (SQLite LIKE ignores ASCII case).</summary>
    private static string LikeContains(string text) =>
        "%" + text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    private static void Apply(Transaction t, TransactionInput input)
    {
        t.TransactionType = input.Type;
        t.AmountCents = input.AmountCents;
        t.Date = input.Date;
        t.AccountId = input.AccountId;
        t.TransferAccountId = input.Type == TransactionType.Transfer ? input.TransferAccountId : null;
        t.PaidFromGoalId = input.Type == TransactionType.Expense ? input.PaidFromGoalId : null;
        t.CategoryId = input.CategoryId;
        t.Payee = Normalize(input.Payee);
        t.Memo = Normalize(input.Memo);
        t.Status = input.Status;
    }

    /// <summary>
    /// Rejects anything that would record money incorrectly. Archived accounts and categories
    /// cannot be newly chosen, but an existing transaction may keep the ones it already has.
    /// </summary>
    internal static async Task ValidateAsync(LedgerDbContext db, TransactionInput input, Transaction? existing, CancellationToken ct)
    {
        if (!Enum.IsDefined(input.Type))
        {
            throw new LedgerValidationException("Choose whether this is spending, income or a transfer.");
        }
        if (input.AmountCents <= 0)
        {
            throw new LedgerValidationException("Enter an amount greater than zero.");
        }
        if (input.AmountCents > Money.MaxAmountCents)
        {
            throw new LedgerValidationException("That amount is too large.");
        }
        if (input.Date < EarliestDate || input.Date > LatestDate)
        {
            throw new LedgerValidationException("Choose a date between 1900 and 2100.");
        }
        if (Normalize(input.Payee)?.Length > 200)
        {
            throw new LedgerValidationException("Payee names can be up to 200 characters.");
        }
        if (Normalize(input.Memo)?.Length > 1000)
        {
            throw new LedgerValidationException("Notes can be up to 1,000 characters.");
        }

        await RequireUsableAccountAsync(db, input.AccountId, existing?.AccountId, existing?.TransferAccountId, ct);

        if (input.Type == TransactionType.Transfer)
        {
            if (input.TransferAccountId is not { } to)
            {
                throw new LedgerValidationException("Choose the account the money is moving to.");
            }
            if (to == input.AccountId)
            {
                throw new LedgerValidationException("A transfer needs two different accounts.");
            }
            await RequireUsableAccountAsync(db, to, existing?.AccountId, existing?.TransferAccountId, ct);
        }

        if (input.PaidFromGoalId is { } fundId && input.Type == TransactionType.Expense)
        {
            var fund = await db.Goals.AsNoTracking().FirstOrDefaultAsync(g => g.Id == fundId, ct);
            if (fund is not { IsSinkingFund: true } || fund.IsArchived && existing?.PaidFromGoalId != fundId)
            {
                throw new LedgerValidationException("That fund no longer exists.");
            }
            // A fund can't go below zero. When editing, this transaction's own earlier amount is given back first.
            var available = await GoalService.SetAsideAsync(db, fund, existing?.Id, ct);
            if (input.AmountCents > available)
            {
                throw new LedgerValidationException($"“{fund.Name}” has {Money.Format(available)} set aside, less than {Money.Format(input.AmountCents)}. Set more aside first, or don't pay this from the fund.");
            }
        }

        if (input.CategoryId is { } categoryId)
        {
            var category = await db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == categoryId, ct)
                ?? throw new LedgerValidationException("That category no longer exists.");
            if (category.IsArchived && existing?.CategoryId != categoryId)
            {
                throw new LedgerValidationException($"“{category.Name}” is archived. Restore it in Settings to use it again.");
            }
            var expected = input.Type switch
            {
                TransactionType.Expense or TransactionType.Refund => CategoryType.Expense,
                TransactionType.Income => CategoryType.Income,
                _ => CategoryType.Savings,
            };
            if (category.CategoryType != expected)
            {
                throw new LedgerValidationException(input.Type switch
                {
                    TransactionType.Expense => "Spending needs a spending category.",
                    TransactionType.Refund => "A refund goes back into a spending category.",
                    TransactionType.Income => "Income needs an income category.",
                    _ => "Transfers can only use a savings category.",
                });
            }
        }
    }

    private static async Task RequireUsableAccountAsync(
        LedgerDbContext db, int accountId, int? existingAccountId, int? existingTransferAccountId, CancellationToken ct)
    {
        var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId, ct)
            ?? throw new LedgerValidationException("That account no longer exists.");
        if (account.IsArchived && accountId != existingAccountId && accountId != existingTransferAccountId)
        {
            throw new LedgerValidationException($"“{account.Name}” is archived. Restore it on the Accounts page to use it again.");
        }
    }

    private static string? Normalize(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

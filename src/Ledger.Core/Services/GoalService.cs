using Ledger.Core.Data;
using Ledger.Core.Forecasting;
using Ledger.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Core.Services;

/// <summary>Fields the user supplies for a goal.</summary>
public sealed record GoalInput(
    string Name,
    long TargetAmountCents,
    DateOnly? TargetDate = null,
    GoalTracking Tracking = GoalTracking.Manual,
    long StartingAmountCents = 0,
    int? AccountId = null,
    bool IsSinkingFund = false,
    long? MonthlySetAsideCents = null,
    int? CategoryId = null);

/// <summary>A goal with its progress worked out.</summary>
public sealed record GoalProgress(Goal Goal, long CurrentCents, DateOnly Today)
{
    public long TargetCents => Goal.TargetAmountCents;
    public long RemainingCents => GoalMath.Remaining(TargetCents, CurrentCents);
    public int Percent => GoalMath.Percent(TargetCents, CurrentCents);
    public double Fraction => GoalMath.Fraction(TargetCents, CurrentCents);
    public bool IsReached => CurrentCents >= TargetCents;
    public bool IsPastDue => !IsReached && Goal.TargetDate is { } d && d < Today;
    public long? SuggestedMonthlyCents => GoalMath.SuggestedMonthly(TargetCents, CurrentCents, Today, Goal.TargetDate);
}

/// <summary>
/// Savings goals and sinking funds. Progress is derived: a manual goal is its starting amount plus
/// contributions, minus spending paid from it (sinking funds); an account-backed goal is that
/// account's actual balance. Contributions are earmarks and never change account balances.
/// </summary>
public sealed class GoalService(IDbContextFactory<LedgerDbContext> contextFactory, AccountService accounts, TimeProvider time)
{
    private DateOnly Today => DateOnly.FromDateTime(time.GetLocalNow().Date);

    public async Task<IReadOnlyList<GoalProgress>> GetGoalsAsync(bool includeArchived = false, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var goals = await db.Goals.AsNoTracking()
            .Include(g => g.Account)
            .Where(g => includeArchived || !g.IsArchived)
            .OrderBy(g => g.IsArchived).ThenBy(g => g.SortOrder).ThenBy(g => g.Name)
            .ToListAsync(ct);
        var contributed = await db.GoalContributions.AsNoTracking()
            .GroupBy(c => c.GoalId)
            .Select(g => new { GoalId = g.Key, Total = g.Sum(c => c.AmountCents) })
            .ToDictionaryAsync(x => x.GoalId, x => x.Total, ct);
        var spent = await SpentFromFundsAsync(db, null, ct);
        contributed = contributed.Keys.Union(spent.Keys)
            .ToDictionary(k => k, k => contributed.GetValueOrDefault(k) - spent.GetValueOrDefault(k));

        var today = Today;
        var balances = goals.Any(g => g.Tracking == GoalTracking.AccountBalance)
            ? (await accounts.GetAccountBalancesAsync(includeArchived: true, asOf: today, ct: ct)).ToDictionary(b => b.Account.Id, b => b.BalanceCents)
            : [];

        return goals.Select(g => new GoalProgress(g, CurrentFor(g, contributed, balances), today)).ToList();
    }

    public async Task<GoalProgress> GetAsync(int id, CancellationToken ct = default) =>
        (await GetGoalsAsync(includeArchived: true, ct)).FirstOrDefault(p => p.Goal.Id == id)
        ?? throw new LedgerValidationException("That goal no longer exists.");

    public async Task<Goal> CreateAsync(GoalInput input, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await ValidateAsync(db, input, existingId: null, ct);
        var goal = new Goal { Name = "" };
        Apply(goal, input);
        goal.SortOrder = (await db.Goals.MaxAsync(g => (int?)g.SortOrder, ct) ?? -1) + 1;
        db.Goals.Add(goal);
        await db.SaveChangesAsync(ct);
        return goal;
    }

    /// <summary>
    /// Changes a goal. Switching a manual goal to an account keeps its contributions on file (they
    /// count again if it's switched back).
    /// </summary>
    public async Task UpdateAsync(int id, GoalInput input, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var goal = await db.Goals.FirstOrDefaultAsync(g => g.Id == id, ct)
            ?? throw new LedgerValidationException("That goal no longer exists.");
        await ValidateAsync(db, input, id, ct);
        Apply(goal, input);
        await db.SaveChangesAsync(ct);
    }

    public async Task SetArchivedAsync(int id, bool archived, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var goal = await db.Goals.FirstOrDefaultAsync(g => g.Id == id, ct)
            ?? throw new LedgerValidationException("That goal no longer exists.");
        if (!archived)
        {
            await ValidateAsync(db, ToInput(goal), id, ct, restoring: true);
        }
        goal.IsArchived = archived;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Deletes a goal and its contributions. No transaction or balance is affected.</summary>
    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var goal = await db.Goals.FirstOrDefaultAsync(g => g.Id == id, ct)
            ?? throw new LedgerValidationException("That goal no longer exists.");
        // Spending paid from a deleted fund stays spending; it just isn't paid from anything now.
        foreach (var t in await db.Transactions.IgnoreQueryFilters().Where(t => t.PaidFromGoalId == id).ToListAsync(ct))
        {
            t.PaidFromGoalId = null;
        }
        db.Goals.Remove(goal);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Adds (positive) or takes out (negative) money from a manual goal. Taking out more than the
    /// goal holds is refused.
    /// </summary>
    public async Task<GoalContribution> AddContributionAsync(int goalId, long signedCents, DateOnly date, string? note = null, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var goal = await db.Goals.AsNoTracking().FirstOrDefaultAsync(g => g.Id == goalId, ct)
            ?? throw new LedgerValidationException("That goal no longer exists.");
        if (goal.Tracking != GoalTracking.Manual)
        {
            throw new LedgerValidationException($"“{goal.Name}” follows an account's balance. Move money into that account instead.");
        }
        if (signedCents == 0)
        {
            throw new LedgerValidationException("Enter an amount greater than zero.");
        }
        if (Math.Abs(signedCents) > Money.MaxAmountCents)
        {
            throw new LedgerValidationException("That amount is too large.");
        }
        if (date.Year is < 1900 or > 2100)
        {
            throw new LedgerValidationException("Choose a date between 1900 and 2100.");
        }
        var trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (trimmed?.Length > 200)
        {
            throw new LedgerValidationException("Notes can be up to 200 characters.");
        }

        var current = await SetAsideAsync(db, goal, null, ct);
        if (current + signedCents < 0)
        {
            throw new LedgerValidationException($"“{goal.Name}” only has {Money.Format(current)} set aside.");
        }

        var contribution = new GoalContribution
        {
            GoalId = goalId,
            Date = date,
            AmountCents = signedCents,
            Note = trimmed,
            CreatedAt = time.GetUtcNow().UtcDateTime,
        };
        db.GoalContributions.Add(contribution);
        await db.SaveChangesAsync(ct);
        return contribution;
    }

    /// <summary>A manual goal's contributions, newest first.</summary>
    public async Task<IReadOnlyList<GoalContribution>> GetContributionsAsync(int goalId, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.GoalContributions.AsNoTracking()
            .Where(c => c.GoalId == goalId)
            .OrderByDescending(c => c.Date).ThenByDescending(c => c.Id)
            .ToListAsync(ct);
    }

    /// <summary>Removes a contribution entered by mistake. Refused if the goal would drop below zero.</summary>
    public async Task DeleteContributionAsync(int contributionId, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var contribution = await db.GoalContributions.Include(c => c.Goal).FirstOrDefaultAsync(c => c.Id == contributionId, ct)
            ?? throw new LedgerValidationException("That entry no longer exists.");
        var goal = contribution.Goal!;
        var current = await SetAsideAsync(db, goal, null, ct);
        if (current - contribution.AmountCents < 0)
        {
            throw new LedgerValidationException("Removing that would take the goal below zero. Remove the money taken out first.");
        }
        db.GoalContributions.Remove(contribution);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Active sinking funds with what each has set aside now.</summary>
    public async Task<IReadOnlyList<GoalProgress>> GetFundsAsync(CancellationToken ct = default) =>
        (await GetGoalsAsync(includeArchived: false, ct)).Where(p => p.Goal.IsSinkingFund).ToList();

    /// <summary>Money put into each fund during a month (set-asides only; spending isn't netted).</summary>
    public async Task<IReadOnlyDictionary<int, long>> GetSetAsideInMonthAsync(int year, int month, CancellationToken ct = default)
    {
        var from = new DateOnly(year, month, 1);
        var to = from.AddMonths(1).AddDays(-1);
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.GoalContributions.AsNoTracking()
            .Where(c => c.Date >= from && c.Date <= to && c.AmountCents > 0 && c.Goal!.IsSinkingFund)
            .GroupBy(c => c.GoalId)
            .Select(g => new { g.Key, Total = g.Sum(c => c.AmountCents) })
            .ToDictionaryAsync(x => x.Key, x => x.Total, ct);
    }

    /// <summary>Sets aside a fund's monthly amount today (Budget page).</summary>
    public async Task SetAsideMonthlyAsync(int goalId, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var goal = await db.Goals.AsNoTracking().FirstOrDefaultAsync(g => g.Id == goalId, ct)
            ?? throw new LedgerValidationException("That fund no longer exists.");
        if (!goal.IsSinkingFund || goal.MonthlySetAsideCents is not > 0)
        {
            throw new LedgerValidationException($"“{goal.Name}” has no monthly amount to set aside. Add one in Edit.");
        }
        await AddContributionAsync(goalId, goal.MonthlySetAsideCents.Value, Today, "Monthly set-aside", ct);
    }

    /// <summary>What a manual goal holds now: starting amount plus contributions, minus spending paid from it (optionally leaving one transaction out).</summary>
    internal static async Task<long> SetAsideAsync(LedgerDbContext db, Goal goal, int? exceptTransactionId, CancellationToken ct)
    {
        var contributed = await db.GoalContributions.Where(c => c.GoalId == goal.Id).SumAsync(c => c.AmountCents, ct);
        var spent = (await SpentFromFundsAsync(db, exceptTransactionId, ct)).GetValueOrDefault(goal.Id);
        return goal.StartingAmountCents + contributed - spent;
    }

    private static async Task<Dictionary<int, long>> SpentFromFundsAsync(LedgerDbContext db, int? exceptTransactionId, CancellationToken ct) =>
        await db.Transactions.AsNoTracking()
            .Where(t => t.PaidFromGoalId != null && t.Id != exceptTransactionId)
            .GroupBy(t => t.PaidFromGoalId!.Value)
            .Select(g => new { g.Key, Total = g.Sum(t => t.AmountCents) })
            .ToDictionaryAsync(x => x.Key, x => x.Total, ct);

    private static long CurrentFor(Goal goal, IReadOnlyDictionary<int, long> contributed, IReadOnlyDictionary<int, long> balances) =>
        goal.Tracking == GoalTracking.AccountBalance && goal.AccountId is { } accountId
            ? Math.Max(0, balances.GetValueOrDefault(accountId))
            : goal.StartingAmountCents + contributed.GetValueOrDefault(goal.Id);

    private static void Apply(Goal goal, GoalInput input)
    {
        goal.Name = input.Name.Trim();
        goal.TargetAmountCents = input.TargetAmountCents;
        goal.TargetDate = input.TargetDate;
        goal.Tracking = input.Tracking;
        goal.AccountId = input.AccountId;
        goal.StartingAmountCents = input.Tracking == GoalTracking.Manual ? input.StartingAmountCents : 0;
        goal.IsSinkingFund = input.IsSinkingFund;
        goal.MonthlySetAsideCents = input.IsSinkingFund && input.MonthlySetAsideCents is > 0 ? input.MonthlySetAsideCents : null;
        goal.CategoryId = input.IsSinkingFund ? input.CategoryId : null;
    }

    private static GoalInput ToInput(Goal g) => new(g.Name, g.TargetAmountCents, g.TargetDate, g.Tracking, g.StartingAmountCents, g.AccountId,
        g.IsSinkingFund, g.MonthlySetAsideCents, g.CategoryId);

    private static async Task ValidateAsync(LedgerDbContext db, GoalInput input, int? existingId, CancellationToken ct, bool restoring = false)
    {
        var name = input.Name?.Trim() ?? "";
        if (name.Length == 0)
        {
            throw new LedgerValidationException("Give the goal a name, like “Emergency fund”.");
        }
        if (name.Length > 100)
        {
            throw new LedgerValidationException("Names can be up to 100 characters.");
        }
        if (input.TargetAmountCents <= 0)
        {
            throw new LedgerValidationException("Enter a target greater than zero.");
        }
        if (input.TargetAmountCents > Money.MaxAmountCents || input.StartingAmountCents > Money.MaxAmountCents)
        {
            throw new LedgerValidationException("That amount is too large.");
        }
        if (input.StartingAmountCents < 0)
        {
            throw new LedgerValidationException("The amount already saved can't be negative.");
        }
        if (input.TargetDate is { Year: < 1900 or > 2100 })
        {
            throw new LedgerValidationException("Choose a target date between 1900 and 2100.");
        }
        if (!Enum.IsDefined(input.Tracking))
        {
            throw new LedgerValidationException("Choose how progress is tracked.");
        }
        if (input.IsSinkingFund)
        {
            if (input.Tracking != GoalTracking.Manual)
            {
                throw new LedgerValidationException("A sinking fund keeps its own set-aside, so it can't follow an account's balance.");
            }
            if (input.MonthlySetAsideCents is < 0 || input.MonthlySetAsideCents > Money.MaxAmountCents)
            {
                throw new LedgerValidationException("Enter a monthly amount of zero or more.");
            }
            if (input.CategoryId is { } categoryId
                && !await db.Categories.AsNoTracking().AnyAsync(c => c.Id == categoryId && c.CategoryType == CategoryType.Expense, ct))
            {
                throw new LedgerValidationException("A fund pays for a spending category.");
            }
        }

        var lower = name.ToLowerInvariant();
        var clash = await db.Goals.AsNoTracking()
            .Where(g => !g.IsArchived && g.Id != existingId)
            .AnyAsync(g => g.Name.ToLower() == lower, ct);
        if (clash)
        {
            throw new LedgerValidationException(restoring
                ? $"You already have a goal called “{name}”. Rename one of them first."
                : $"You already have a goal called “{name}”.");
        }

        if (input.AccountId is { } accountId)
        {
            var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId, ct)
                ?? throw new LedgerValidationException("That account no longer exists.");
            if (input.Tracking == GoalTracking.AccountBalance)
            {
                if (account.IsLiability)
                {
                    throw new LedgerValidationException("A credit card's balance is money owed, so it can't track savings.");
                }
                var other = await db.Goals.AsNoTracking()
                    .Where(g => !g.IsArchived && g.Id != existingId && g.Tracking == GoalTracking.AccountBalance && g.AccountId == accountId)
                    .Select(g => g.Name).FirstOrDefaultAsync(ct);
                if (other is not null)
                {
                    throw new LedgerValidationException($"“{other}” already follows {account.Name}'s balance. Each account can back one goal, so the same money isn't counted twice.");
                }
            }
        }
        else if (input.Tracking == GoalTracking.AccountBalance)
        {
            throw new LedgerValidationException("Choose the account whose balance tracks this goal.");
        }
    }
}

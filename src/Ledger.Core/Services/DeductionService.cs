using Ledger.Core.Data;
using Ledger.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Core.Services;

/// <summary>
/// A deduction as the user edits it. <see cref="Id"/> is null for a new one. Exactly one of
/// <see cref="AmountCents"/> (fixed per paycheck) or <see cref="PercentBasisPoints"/> (share of gross) is used.
/// </summary>
public sealed record DeductionInput(int? Id, string Name, DeductionKind Kind, long AmountCents, int? PercentBasisPoints, DateOnly StartDate);

/// <summary>
/// Paycheck deductions per job. Saving the edited list keeps history: a deduction that already
/// applied to past pay periods is never changed in place. It's closed the day before and a new
/// version starts today, so earlier estimates stay exactly as they were.
/// </summary>
public sealed class DeductionService(IDbContextFactory<LedgerDbContext> contextFactory)
{
    /// <summary>Deductions still in effect on <paramref name="today"/> or starting later.</summary>
    public async Task<IReadOnlyList<JobDeduction>> GetCurrentAsync(int jobId, DateOnly today, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.JobDeductions.AsNoTracking()
            .Where(d => d.JobId == jobId && (d.EndDate == null || d.EndDate >= today))
            .OrderBy(d => d.Kind).ThenBy(d => d.Id)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Saves the job's current deductions as edited:
    /// <list type="bullet">
    /// <item>New ones are added from their start date.</item>
    /// <item>Removed ones end yesterday, or are deleted if they never applied.</item>
    /// <item>Changed amounts, percents or kinds end yesterday and restart today, unless they
    /// haven't started yet (then they're edited).</item>
    /// <item>A rename alone is applied everywhere, since it doesn't change any money.</item>
    /// </list>
    /// </summary>
    public async Task SaveAsync(int jobId, IReadOnlyList<DeductionInput> inputs, DateOnly today, CancellationToken ct = default)
    {
        foreach (var input in inputs)
        {
            Validate(input);
        }

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        if (!await db.Jobs.AnyAsync(j => j.Id == jobId, ct))
        {
            throw new LedgerValidationException("That job no longer exists.");
        }

        var current = await db.JobDeductions
            .Where(d => d.JobId == jobId && (d.EndDate == null || d.EndDate >= today))
            .ToListAsync(ct);
        var yesterday = today.AddDays(-1);

        foreach (var existing in current)
        {
            var edited = inputs.FirstOrDefault(i => i.Id == existing.Id);
            var started = existing.StartDate < today;
            if (edited is null)
            {
                if (started) existing.EndDate = yesterday;
                else db.JobDeductions.Remove(existing);
                continue;
            }

            var name = edited.Name.Trim();
            var percent = edited.Kind == DeductionKind.PreTaxHealth ? null : edited.PercentBasisPoints;
            var moneyChanged = existing.Kind != edited.Kind || existing.PercentBasisPoints != percent
                               || (percent is null && existing.AmountCents != edited.AmountCents);
            if (!moneyChanged)
            {
                existing.Name = name;
                if (!started) existing.StartDate = edited.StartDate;
            }
            else if (!started)
            {
                Apply(existing, edited);
            }
            else
            {
                existing.EndDate = yesterday;
                var next = new JobDeduction { JobId = jobId, Name = name };
                Apply(next, edited with { StartDate = today });
                db.JobDeductions.Add(next);
            }
        }

        foreach (var added in inputs.Where(i => i.Id is null || current.All(c => c.Id != i.Id)))
        {
            var d = new JobDeduction { JobId = jobId, Name = added.Name.Trim() };
            Apply(d, added);
            db.JobDeductions.Add(d);
        }

        await db.SaveChangesAsync(ct);
    }

    private static void Apply(JobDeduction d, DeductionInput input)
    {
        d.Name = input.Name.Trim();
        d.Kind = input.Kind;
        // Section 125 benefits are fixed premiums, never a percentage.
        d.PercentBasisPoints = input.Kind == DeductionKind.PreTaxHealth ? null : input.PercentBasisPoints;
        d.AmountCents = d.PercentBasisPoints is null ? input.AmountCents : 0;
        d.StartDate = input.StartDate;
        d.EndDate = null;
    }

    private static void Validate(DeductionInput input)
    {
        var name = input.Name?.Trim() ?? "";
        if (name.Length == 0)
        {
            throw new LedgerValidationException("Give each deduction a name, like “Dental” or “401(k)”.");
        }
        if (name.Length > 60)
        {
            throw new LedgerValidationException("Deduction names can be up to 60 characters.");
        }
        if (!Enum.IsDefined(input.Kind))
        {
            throw new LedgerValidationException($"Choose what kind of deduction “{name}” is.");
        }
        if (input.PercentBasisPoints is { } bps && input.Kind != DeductionKind.PreTaxHealth)
        {
            if (bps is < 1 or > 10_000)
            {
                throw new LedgerValidationException($"“{name}” must be between 0.01% and 100% of pay.");
            }
        }
        else if (input.AmountCents <= 0)
        {
            throw new LedgerValidationException($"Enter an amount per paycheck for “{name}”.");
        }
        else if (input.AmountCents > Money.MaxAmountCents)
        {
            throw new LedgerValidationException($"The amount for “{name}” is too large.");
        }
    }
}

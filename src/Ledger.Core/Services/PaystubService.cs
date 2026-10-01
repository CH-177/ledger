using Ledger.Core.Data;
using Ledger.Core.Models;
using Ledger.Core.Payroll;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Core.Services;

/// <summary>The numbers typed from a paystub. Only gross, net and pay date are required.</summary>
public sealed record PaystubInput(
    DateOnly PayDate,
    long GrossCents,
    long NetCents,
    int? PaidMinutes = null,
    DateOnly? StubPeriodStart = null,
    DateOnly? StubPeriodEnd = null,
    long? FederalCents = null,
    long? SocialSecurityCents = null,
    long? MedicareCents = null,
    long? StateCents = null,
    long? StateUnemploymentCents = null,
    long? LocalEitCents = null,
    long? LocalServicesTaxCents = null);

/// <summary>
/// Stores paystubs against the paycheck they belong to and confirms a job's real payday.
/// A paystub never changes the stored estimate or any transaction.
/// </summary>
public sealed class PaystubService(IDbContextFactory<LedgerDbContext> contextFactory)
{
    /// <summary>Saves the paystub for the job's pay period starting <paramref name="periodStart"/>, replacing any earlier one.</summary>
    public async Task<Paystub> SaveAsync(int jobId, DateOnly periodStart, PaystubInput input, CancellationToken ct = default)
    {
        Validate(input);
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        if (!await db.Jobs.AnyAsync(j => j.Id == jobId, ct))
        {
            throw new LedgerValidationException("That job no longer exists.");
        }
        var stub = await db.Paystubs.FirstOrDefaultAsync(s => s.JobId == jobId && s.PeriodStart == periodStart, ct);
        if (stub is null)
        {
            db.Paystubs.Add(stub = new Paystub { JobId = jobId, PeriodStart = periodStart });
        }
        stub.PayDate = input.PayDate;
        stub.GrossCents = input.GrossCents;
        stub.NetCents = input.NetCents;
        stub.PaidMinutes = input.PaidMinutes;
        stub.StubPeriodStart = input.StubPeriodStart;
        stub.StubPeriodEnd = input.StubPeriodEnd;
        stub.FederalCents = input.FederalCents;
        stub.SocialSecurityCents = input.SocialSecurityCents;
        stub.MedicareCents = input.MedicareCents;
        stub.StateCents = input.StateCents;
        stub.StateUnemploymentCents = input.StateUnemploymentCents;
        stub.LocalEitCents = input.LocalEitCents;
        stub.LocalServicesTaxCents = input.LocalServicesTaxCents;
        await db.SaveChangesAsync(ct);
        return stub;
    }

    public async Task DeleteAsync(int jobId, DateOnly periodStart, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await db.Paystubs.Where(s => s.JobId == jobId && s.PeriodStart == periodStart).ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// Sets the job's real pay schedule from a paystub: periods start on <see cref="PaydaySuggestion.PeriodStart"/>'s
    /// cycle, and payday is <see cref="PaydaySuggestion.OffsetDays"/> after each period ends. Marks it confirmed.
    /// Future estimates follow; recorded paychecks and paystubs are untouched.
    /// </summary>
    public async Task ConfirmPaydayAsync(int jobId, PaydaySuggestion payday, CancellationToken ct = default)
    {
        if (payday.OffsetDays is < 0 or > 31)
        {
            throw new LedgerValidationException("Payday must be within 31 days after the pay period ends.");
        }
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, ct)
            ?? throw new LedgerValidationException("That job no longer exists.");
        job.PayPeriodAnchor = payday.PeriodStart;
        job.PaydayOffsetDays = payday.OffsetDays;
        job.IsPaydayConfirmed = true;
        await db.SaveChangesAsync(ct);
    }

    private static void Validate(PaystubInput input)
    {
        long?[] amounts = [input.GrossCents, input.NetCents, input.FederalCents, input.SocialSecurityCents, input.MedicareCents,
            input.StateCents, input.StateUnemploymentCents, input.LocalEitCents, input.LocalServicesTaxCents];
        if (amounts.Any(a => a is < 0 or > Money.MaxAmountCents))
        {
            throw new LedgerValidationException("Paystub amounts must be between $0 and a sensible maximum.");
        }
        if (input.NetCents > input.GrossCents)
        {
            throw new LedgerValidationException("Take-home pay can't be more than gross pay. Check the two numbers.");
        }
        if (input.PaidMinutes is < 0 or > 31 * 24 * 60)
        {
            throw new LedgerValidationException("Enter the paid hours shown on the paystub.");
        }
        if (input.PayDate.Year is < 1900 or > 2100)
        {
            throw new LedgerValidationException("Choose the pay date on the paystub.");
        }
        if (input.StubPeriodStart is { } s && input.StubPeriodEnd is { } e && e < s)
        {
            throw new LedgerValidationException("The pay period's end can't be before its start.");
        }
    }
}

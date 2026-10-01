using Ledger.Core.Data;
using Ledger.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Core.Services;

/// <summary>An account together with its derived balance.</summary>
public sealed record AccountBalance(Account Account, long BalanceCents);

/// <summary>Assets minus liabilities, where liabilities are reported as a positive amount owed.</summary>
public sealed record NetWorth(long AssetsCents, long LiabilitiesCents)
{
    public long NetWorthCents => AssetsCents - LiabilitiesCents;
}

/// <summary>Fields the user supplies when creating or editing an account.</summary>
public sealed record AccountInput(
    string Name,
    AccountType AccountType,
    long StartingBalanceCents,
    string? InstitutionName = null,
    bool IsIncludedInNetWorth = true,
    DateOnly? BalanceDate = null);

/// <summary>
/// Manages accounts and is the single authoritative source of account balances
/// and net worth.
/// </summary>
public sealed class AccountService(IDbContextFactory<LedgerDbContext> contextFactory, TimeProvider time)
{
    private DateOnly Today => DateOnly.FromDateTime(time.GetLocalNow().DateTime);

    /// <summary>
    /// Returns accounts with their actual balance: starting balance plus every
    /// non-deleted transaction dated on or before <paramref name="asOf"/> (default today).
    /// Future-dated transactions are not yet real and are excluded. When the starting balance is
    /// from a balance date, transactions on or before that date are already in it:
    /// balance(asOf) = starting + movements up to asOf - movements up to the balance date.
    /// </summary>
    public async Task<IReadOnlyList<AccountBalance>> GetAccountBalancesAsync(
        bool includeArchived = false, DateOnly? asOf = null, CancellationToken ct = default)
    {
        var date = asOf ?? Today;
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        var accounts = await db.Accounts.AsNoTracking()
            .Where(a => includeArchived || !a.IsArchived)
            .OrderBy(a => a.SortOrder).ThenBy(a => a.Name)
            .ToListAsync(ct);

        var movements = await GetNetMovementsAsync(db, date, ct);
        var included = new Dictionary<DateOnly, Dictionary<int, long>>();
        foreach (var d in accounts.Where(a => a.BalanceDate is not null).Select(a => a.BalanceDate!.Value).Distinct())
        {
            included[d] = await GetNetMovementsAsync(db, d, ct);
        }

        return accounts
            .Select(a => new AccountBalance(a, checked(a.StartingBalanceCents + movements.GetValueOrDefault(a.Id)
                - (a.BalanceDate is { } d ? included[d].GetValueOrDefault(a.Id) : 0))))
            .ToList();
    }

    /// <summary>Returns the actual balance of one account as of <paramref name="asOf"/> (default today).</summary>
    public async Task<long> GetBalanceAsync(int accountId, DateOnly? asOf = null, CancellationToken ct = default)
    {
        var balances = await GetAccountBalancesAsync(includeArchived: true, asOf, ct);
        return balances.FirstOrDefault(b => b.Account.Id == accountId)?.BalanceCents
            ?? throw new LedgerValidationException("That account no longer exists.");
    }

    /// <summary>
    /// Net worth across accounts flagged as included. Archived accounts still count
    /// if they hold money, since archiving hides an account rather than erasing it.
    /// </summary>
    public async Task<NetWorth> GetNetWorthAsync(DateOnly? asOf = null, CancellationToken ct = default)
    {
        var balances = await GetAccountBalancesAsync(includeArchived: true, asOf, ct);
        long assets = 0, liabilities = 0;
        foreach (var b in balances.Where(b => b.Account.IsIncludedInNetWorth))
        {
            if (b.Account.IsLiability)
            {
                liabilities = checked(liabilities - b.BalanceCents);
            }
            else
            {
                assets = checked(assets + b.BalanceCents);
            }
        }
        return new NetWorth(assets, liabilities);
    }

    public async Task<Account> CreateAsync(AccountInput input, CancellationToken ct = default)
    {
        var name = Validate(input);
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        var account = new Account
        {
            Name = name,
            AccountType = input.AccountType,
            StartingBalanceCents = input.StartingBalanceCents,
            InstitutionName = Normalize(input.InstitutionName),
            IsIncludedInNetWorth = input.IsIncludedInNetWorth,
            BalanceDate = input.BalanceDate,
            SortOrder = await db.Accounts.CountAsync(ct),
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync(ct);
        return account;
    }

    public async Task UpdateAsync(int accountId, AccountInput input, CancellationToken ct = default)
    {
        var name = Validate(input);
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var account = await db.Accounts.FindAsync([accountId], ct)
            ?? throw new LedgerValidationException("That account no longer exists.");

        account.Name = name;
        account.AccountType = input.AccountType;
        account.StartingBalanceCents = input.StartingBalanceCents;
        account.InstitutionName = Normalize(input.InstitutionName);
        account.IsIncludedInNetWorth = input.IsIncludedInNetWorth;
        account.BalanceDate = input.BalanceDate;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Makes the account's balance at the end of <paramref name="date"/> exactly what the bank says.
    /// Everything dated on or before it is then already included; later transactions move it as usual.
    /// </summary>
    public async Task SetBalanceFromBankAsync(int accountId, DateOnly date, long bankBalanceCents, CancellationToken ct = default)
    {
        if (Math.Abs(bankBalanceCents) > Money.MaxAmountCents)
        {
            throw new LedgerValidationException("That balance is too large.");
        }
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var account = await db.Accounts.FindAsync([accountId], ct)
            ?? throw new LedgerValidationException("That account no longer exists.");
        account.StartingBalanceCents = bankBalanceCents;
        account.BalanceDate = date;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Hides an account from everyday views. Its history is kept intact.</summary>
    public async Task SetArchivedAsync(int accountId, bool archived, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var account = await db.Accounts.FindAsync([accountId], ct)
            ?? throw new LedgerValidationException("That account no longer exists.");
        account.IsArchived = archived;
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> AnyAccountsAsync(CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.Accounts.AnyAsync(ct);
    }

    /// <summary>
    /// Net change per account from transactions dated on or before <paramref name="asOf"/>.
    /// Income, refunds and transfers in from outside add to their account; expenses and transfers out to outside subtract; a transfer subtracts from its
    /// source and adds to its destination, so transfers always net to zero overall.
    /// </summary>
    private static async Task<Dictionary<int, long>> GetNetMovementsAsync(
        LedgerDbContext db, DateOnly asOf, CancellationToken ct)
    {
        var inRange = db.Transactions.AsNoTracking().Where(t => t.Date <= asOf);

        var bySource = await inRange
            .GroupBy(t => t.AccountId)
            .Select(g => new
            {
                AccountId = g.Key,
                Net = g.Sum(t => t.TransactionType == TransactionType.Income || t.TransactionType == TransactionType.Refund || t.TransactionType == TransactionType.OutsideIn ? t.AmountCents : -t.AmountCents),
            })
            .ToListAsync(ct);

        var byDestination = await inRange
            .Where(t => t.TransactionType == TransactionType.Transfer)
            .GroupBy(t => t.TransferAccountId!.Value)
            .Select(g => new { AccountId = g.Key, Net = g.Sum(t => t.AmountCents) })
            .ToListAsync(ct);

        var result = new Dictionary<int, long>();
        foreach (var row in bySource.Concat(byDestination))
        {
            result[row.AccountId] = checked(result.GetValueOrDefault(row.AccountId) + row.Net);
        }
        return result;
    }

    private static string Validate(AccountInput input)
    {
        var name = Normalize(input.Name)
            ?? throw new LedgerValidationException("Give the account a name.");
        if (name.Length > 100)
        {
            throw new LedgerValidationException("Account names can be up to 100 characters.");
        }
        if (!Enum.IsDefined(input.AccountType))
        {
            throw new LedgerValidationException("Choose an account type.");
        }
        if (Math.Abs(input.StartingBalanceCents) > Money.MaxAmountCents)
        {
            throw new LedgerValidationException("That starting balance is too large.");
        }
        if (input.InstitutionName?.Trim().Length > 100)
        {
            throw new LedgerValidationException("Institution names can be up to 100 characters.");
        }
        return name;
    }

    private static string? Normalize(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

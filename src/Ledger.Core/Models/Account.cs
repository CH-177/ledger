namespace Ledger.Core.Models;

/// <summary>
/// A place where money physically exists. The current balance is never stored:
/// it is always derived from <see cref="StartingBalanceCents"/> plus transaction history.
/// </summary>
/// <remarks>
/// Balances are signed from the user's point of view. A credit card that is owed
/// $120 has a balance of -12000, so net worth is simply the sum of included balances.
/// </remarks>
public class Account : IAuditable
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public AccountType AccountType { get; set; }
    public string? InstitutionName { get; set; }
    public long StartingBalanceCents { get; set; }

    /// <summary>
    /// The day <see cref="StartingBalanceCents"/> is the bank's balance for (at the end of that day).
    /// Transactions dated on or before it are already inside that number: they stay in spending and
    /// income history but don't move the balance again. Null means the starting balance is from
    /// before every transaction.
    /// </summary>
    public DateOnly? BalanceDate { get; set; }
    public bool IsIncludedInNetWorth { get; set; } = true;
    public bool IsArchived { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Liability accounts are reported as debts rather than assets.</summary>
    public bool IsLiability => AccountType.IsLiability();
}

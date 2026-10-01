namespace Ledger.Core.Models;

// Enum values are persisted as integers. Never renumber an existing member;
// only append new ones.

/// <summary>Where money physically lives.</summary>
public enum AccountType
{
    Checking = 0,
    Savings = 1,
    Cash = 2,
    CreditCard = 3,
    Other = 4,
}

/// <summary>
/// The direction of a transaction. Amounts are always stored as positive
/// magnitudes; the type determines how they affect balances.
/// </summary>
public enum TransactionType
{
    /// <summary>Money arriving in <c>AccountId</c> from outside the user's accounts.</summary>
    Income = 0,

    /// <summary>Money leaving <c>AccountId</c> to outside the user's accounts.</summary>
    Expense = 1,

    /// <summary>
    /// Money moving from <c>AccountId</c> to <c>TransferAccountId</c>.
    /// Never counts as income or spending and never changes net worth.
    /// </summary>
    Transfer = 2,

    /// <summary>
    /// Money coming back into <c>AccountId</c> for something bought: it uses a spending category and
    /// lowers spending in that category and month. Never counts as income.
    /// </summary>
    Refund = 3,

    /// <summary>
    /// A transfer in from someone else's account (e.g. a family member's): money arrives in
    /// <c>AccountId</c> but is neither income nor spending. It does change net worth.
    /// </summary>
    OutsideIn = 4,

    /// <summary>A transfer out to someone else's account: money leaves <c>AccountId</c>; neither spending nor income.</summary>
    OutsideOut = 5,
}

public enum TransactionStatus
{
    Pending = 0,
    Cleared = 1,
    Reconciled = 2,
}

public enum CategoryType
{
    Expense = 0,
    Income = 1,

    /// <summary>Money set aside for goals; typically attached to transfers, never counted as spending.</summary>
    Savings = 2,
}

public enum RecurrenceFrequency
{
    Weekly = 0,
    Biweekly = 1,
    Monthly = 2,
    Quarterly = 3,
    Yearly = 4,
}

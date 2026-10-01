namespace Ledger.Core.Models;

/// <summary>Behaviour that depends only on an account's type.</summary>
public static class AccountTypeRules
{
    /// <summary>Liability accounts hold money owed rather than money owned.</summary>
    public static bool IsLiability(this AccountType type) => type == AccountType.CreditCard;

    /// <summary>
    /// Converts the starting amount a user types into the signed value that is stored.
    /// For a liability the user enters what they owe ($120), which is stored as -12000.
    /// </summary>
    public static long ToStoredBalance(this AccountType type, long enteredCents) =>
        type.IsLiability() ? checked(-enteredCents) : enteredCents;

    /// <summary>The inverse of <see cref="ToStoredBalance"/>: the amount to show in an editor.</summary>
    public static long ToEnteredBalance(this AccountType type, long storedCents) =>
        type.IsLiability() ? checked(-storedCents) : storedCents;
}

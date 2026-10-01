namespace Ledger.Core.Models;

public enum GoalTracking
{
    /// <summary>Progress is the starting amount plus the contributions the user adds or takes out.</summary>
    Manual = 0,

    /// <summary>Progress is the actual balance of <see cref="Goal.AccountId"/> (a dedicated savings account).</summary>
    AccountBalance = 1,
}

/// <summary>
/// Something the user is saving toward. Progress is always derived, never stored as a running
/// total: either from contributions or from an account's balance. Structured so it can later act
/// as a sinking fund (money reserved for a future expense).
/// </summary>
public class Goal : IAuditable
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public long TargetAmountCents { get; set; }

    /// <summary>Already saved before tracking began (manual goals only).</summary>
    public long StartingAmountCents { get; set; }

    public DateOnly? TargetDate { get; set; }

    public GoalTracking Tracking { get; set; }

    /// <summary>
    /// For <see cref="GoalTracking.AccountBalance"/>, the account whose balance is the progress.
    /// For manual goals, optionally where the money is kept (informational).
    /// </summary>
    public int? AccountId { get; set; }
    public Account? Account { get; set; }

    /// <summary>For a sinking fund, the spending category it pays for (e.g. Transportation › Maintenance).</summary>
    public int? CategoryId { get; set; }
    public Category? Category { get; set; }

    /// <summary>
    /// Money set aside for a cost you know is coming (car repairs, gifts, a yearly bill), so it isn't
    /// mistaken for money you can spend. Always tracked manually; spending "paid from" it lowers it.
    /// </summary>
    public bool IsSinkingFund { get; set; }

    /// <summary>How much a sinking fund sets aside each month (offered on the Budget page).</summary>
    public long? MonthlySetAsideCents { get; set; }

    public bool IsArchived { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Money the user set aside for (positive) or took back from (negative) a manual goal. This is an
/// earmark, not a movement between accounts: it never changes any balance.
/// </summary>
public class GoalContribution
{
    public int Id { get; set; }
    public int GoalId { get; set; }
    public Goal? Goal { get; set; }
    public DateOnly Date { get; set; }

    /// <summary>Signed cents: positive adds to the goal, negative takes out. Never zero.</summary>
    public long AmountCents { get; set; }

    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; }
}

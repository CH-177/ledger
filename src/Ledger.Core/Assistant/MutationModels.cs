namespace Ledger.Core.Assistant;

/// <summary>What a proposed write will do once the user Approves it in Ledger.</summary>
public enum MutationKind
{
    CreateTransaction,
    UpdateTransaction,
    DeleteTransaction,
    RestoreTransaction,
    CreateAccount,
    UpdateAccount,
    ArchiveAccount,
    SetBankBalance,
    CreateCategory,
    RenameCategory,
    MoveCategory,
    ArchiveCategory,
    SetBudget,
    CopyBudget,
    CreateRecurring,
    UpdateRecurring,
    SetRecurringActive,
    DeleteRecurring,
    SkipRecurring,
    UnskipRecurring,
    RecordRecurring,
    CreateGoal,
    UpdateGoal,
    ArchiveGoal,
    DeleteGoal,
    ContributeGoal,
    DeleteContribution,
    SetAsideMonthly,
    CreateJob,
    UpdateJob,
    ArchiveJob,
    AddShift,
    UpdateShift,
    DeleteShift,
    SetHomeTax,
    SaveDeductions,
    SavePaystub,
    DeletePaystub,
}

/// <summary>Lifecycle of a write proposal waiting for the user.</summary>
public enum ProposalStatus
{
    Pending,
    Approved,
    Denied,
    Expired,
    Failed,
}

/// <summary>A write the Grokbot asked for, before or after the user decides.</summary>
public sealed class MutationProposal
{
    public required string Id { get; init; }
    public required MutationKind Kind { get; init; }
    public required string ToolName { get; init; }
    public required string Summary { get; init; }
    public required string PayloadJson { get; init; }
    public ProposalStatus Status { get; set; } = ProposalStatus.Pending;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public string? ResultJson { get; set; }
    public string? Error { get; set; }
}

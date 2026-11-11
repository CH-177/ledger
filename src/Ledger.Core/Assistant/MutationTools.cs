using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ledger.Core.Models;
using Ledger.Core.Services;

namespace Ledger.Core.Assistant;

/// <summary>
/// Write tools for the Grokbot bridge. Parsing a tool never changes data; <see cref="ApplyAsync"/>
/// runs only after the user Approves the proposal in Ledger.
/// </summary>
public sealed class MutationTools(
    TransactionService transactions,
    AccountService accounts,
    CategoryService categories,
    BudgetService budgets,
    RecurringService recurring,
    GoalService goals,
    IncomeService income,
    DeductionService deductions,
    PaystubService paystubs,
    TimeProvider time)
{
    private DateOnly Today => DateOnly.FromDateTime(time.GetLocalNow().Date);

    public static IReadOnlyList<AssistantTool> Definitions { get; } =
    [
        new("propose_create_transaction",
            "Propose adding a transaction. Requires Approve in Ledger. Amounts are dollars.",
            """{"type":"object","properties":{"kind":{"type":"string","enum":["spending","income","transfer","refund"]},"amount":{"type":"number","exclusiveMinimum":0},"date":{"type":"string","description":"YYYY-MM-DD"},"account":{"type":"string"},"transfer_account":{"type":"string"},"category":{"type":"string"},"payee":{"type":"string"},"note":{"type":"string"},"pending":{"type":"boolean"}},"required":["kind","amount","account"],"additionalProperties":false}"""),
        new("propose_update_transaction",
            "Propose editing a transaction by id. Requires Approve in Ledger.",
            """{"type":"object","properties":{"id":{"type":"integer"},"kind":{"type":"string","enum":["spending","income","transfer","refund"]},"amount":{"type":"number","exclusiveMinimum":0},"date":{"type":"string"},"account":{"type":"string"},"transfer_account":{"type":"string"},"category":{"type":"string"},"payee":{"type":"string"},"note":{"type":"string"},"pending":{"type":"boolean"}},"required":["id","kind","amount","account"],"additionalProperties":false}"""),
        new("propose_delete_transaction",
            "Propose soft-deleting a transaction (can be restored). Requires Approve.",
            """{"type":"object","properties":{"id":{"type":"integer"}},"required":["id"],"additionalProperties":false}"""),
        new("propose_restore_transaction",
            "Propose restoring a soft-deleted transaction. Requires Approve.",
            """{"type":"object","properties":{"id":{"type":"integer"}},"required":["id"],"additionalProperties":false}"""),
        new("propose_create_account",
            "Propose creating an account. Requires Approve.",
            """{"type":"object","properties":{"name":{"type":"string"},"type":{"type":"string","enum":["checking","savings","credit_card","cash","other"]},"starting_balance":{"type":"number"},"institution":{"type":"string"},"include_in_net_worth":{"type":"boolean"},"balance_date":{"type":"string"}},"required":["name","type"],"additionalProperties":false}"""),
        new("propose_update_account",
            "Propose updating an account. Requires Approve.",
            """{"type":"object","properties":{"id":{"type":"integer"},"name":{"type":"string"},"type":{"type":"string","enum":["checking","savings","credit_card","cash","other"]},"starting_balance":{"type":"number"},"institution":{"type":"string"},"include_in_net_worth":{"type":"boolean"},"balance_date":{"type":"string"}},"required":["id","name","type"],"additionalProperties":false}"""),
        new("propose_archive_account",
            "Propose archiving or un-archiving an account. Requires Approve.",
            """{"type":"object","properties":{"id":{"type":"integer"},"archived":{"type":"boolean"}},"required":["id","archived"],"additionalProperties":false}"""),
        new("propose_set_bank_balance",
            "Propose setting an account's starting balance from the bank on a date. Requires Approve.",
            """{"type":"object","properties":{"id":{"type":"integer"},"date":{"type":"string"},"balance":{"type":"number"}},"required":["id","date","balance"],"additionalProperties":false}"""),
        new("propose_create_category",
            "Propose creating a category or subcategory. Requires Approve.",
            """{"type":"object","properties":{"name":{"type":"string"},"type":{"type":"string","enum":["spending","income","savings"]},"parent":{"type":"string","description":"Parent group name for a subcategory."}},"required":["name","type"],"additionalProperties":false}"""),
        new("propose_rename_category",
            "Propose renaming a category by id. Requires Approve.",
            """{"type":"object","properties":{"id":{"type":"integer"},"name":{"type":"string"}},"required":["id","name"],"additionalProperties":false}"""),
        new("propose_move_category",
            "Propose moving a category under another group (or to top level). Requires Approve.",
            """{"type":"object","properties":{"id":{"type":"integer"},"parent":{"type":"string","description":"Parent group name, or omit/null for top level."}},"required":["id"],"additionalProperties":false}"""),
        new("propose_archive_category",
            "Propose archiving or restoring a category. Requires Approve.",
            """{"type":"object","properties":{"id":{"type":"integer"},"archived":{"type":"boolean"}},"required":["id","archived"],"additionalProperties":false}"""),
        new("propose_set_budget",
            "Propose setting a spending group's planned amount for a month (0 removes it). Requires Approve.",
            """{"type":"object","properties":{"month":{"type":"string","description":"YYYY-MM"},"group":{"type":"string"},"planned":{"type":"number","minimum":0}},"required":["group","planned"],"additionalProperties":false}"""),
        new("propose_copy_budget",
            "Propose copying last month's budget plan into a month. Requires Approve.",
            """{"type":"object","properties":{"month":{"type":"string","description":"YYYY-MM"}},"additionalProperties":false}"""),
        new("propose_create_recurring",
            "Propose a recurring bill, income or transfer. Requires Approve.",
            """{"type":"object","properties":{"name":{"type":"string"},"kind":{"type":"string","enum":["spending","income","transfer"]},"amount":{"type":"number","exclusiveMinimum":0},"frequency":{"type":"string","enum":["weekly","biweekly","monthly","quarterly","yearly"]},"start_date":{"type":"string"},"account":{"type":"string"},"transfer_account":{"type":"string"},"category":{"type":"string"},"end_date":{"type":"string"},"estimated":{"type":"boolean"},"note":{"type":"string"},"auto_record":{"type":"boolean"}},"required":["name","kind","amount","frequency","start_date","account"],"additionalProperties":false}"""),
        new("propose_update_recurring",
            "Propose updating a recurring rule by id. Requires Approve.",
            """{"type":"object","properties":{"id":{"type":"integer"},"name":{"type":"string"},"kind":{"type":"string","enum":["spending","income","transfer"]},"amount":{"type":"number","exclusiveMinimum":0},"frequency":{"type":"string","enum":["weekly","biweekly","monthly","quarterly","yearly"]},"start_date":{"type":"string"},"account":{"type":"string"},"transfer_account":{"type":"string"},"category":{"type":"string"},"end_date":{"type":"string"},"estimated":{"type":"boolean"},"note":{"type":"string"},"auto_record":{"type":"boolean"}},"required":["id","name","kind","amount","frequency","start_date","account"],"additionalProperties":false}"""),
        new("propose_set_recurring_active",
            "Propose pausing or resuming a recurring rule. Requires Approve.",
            """{"type":"object","properties":{"id":{"type":"integer"},"active":{"type":"boolean"}},"required":["id","active"],"additionalProperties":false}"""),
        new("propose_delete_recurring",
            "Propose deleting a recurring rule with no recorded payments. Requires Approve.",
            """{"type":"object","properties":{"id":{"type":"integer"}},"required":["id"],"additionalProperties":false}"""),
        new("propose_skip_recurring",
            "Propose skipping a recurring occurrence on a date. Requires Approve.",
            """{"type":"object","properties":{"id":{"type":"integer"},"date":{"type":"string"}},"required":["id","date"],"additionalProperties":false}"""),
        new("propose_unskip_recurring",
            "Propose un-skipping a recurring occurrence. Requires Approve.",
            """{"type":"object","properties":{"id":{"type":"integer"},"date":{"type":"string"}},"required":["id","date"],"additionalProperties":false}"""),
        new("propose_record_recurring",
            "Propose linking an existing transaction as the payment of a recurring date. Requires Approve.",
            """{"type":"object","properties":{"id":{"type":"integer"},"date":{"type":"string"},"transaction_id":{"type":"integer"}},"required":["id","date","transaction_id"],"additionalProperties":false}"""),
        new("propose_create_goal",
            "Propose a savings goal or sinking fund. Requires Approve.",
            """{"type":"object","properties":{"name":{"type":"string"},"target":{"type":"number","exclusiveMinimum":0},"target_date":{"type":"string"},"tracking":{"type":"string","enum":["manual","account"]},"starting":{"type":"number","minimum":0},"account":{"type":"string"},"sinking_fund":{"type":"boolean"},"monthly_set_aside":{"type":"number","minimum":0},"category":{"type":"string"}},"required":["name","target"],"additionalProperties":false}"""),
        new("propose_update_goal",
            "Propose updating a goal by id. Requires Approve.",
            """{"type":"object","properties":{"id":{"type":"integer"},"name":{"type":"string"},"target":{"type":"number","exclusiveMinimum":0},"target_date":{"type":"string"},"tracking":{"type":"string","enum":["manual","account"]},"starting":{"type":"number","minimum":0},"account":{"type":"string"},"sinking_fund":{"type":"boolean"},"monthly_set_aside":{"type":"number","minimum":0},"category":{"type":"string"}},"required":["id","name","target"],"additionalProperties":false}"""),
        new("propose_archive_goal",
            "Propose archiving or restoring a goal. Requires Approve.",
            """{"type":"object","properties":{"id":{"type":"integer"},"archived":{"type":"boolean"}},"required":["id","archived"],"additionalProperties":false}"""),
        new("propose_delete_goal",
            "Propose deleting a goal. Requires Approve.",
            """{"type":"object","properties":{"id":{"type":"integer"}},"required":["id"],"additionalProperties":false}"""),
        new("propose_contribute_goal",
            "Propose adding or removing money earmarked on a manual goal (signed dollars). Requires Approve.",
            """{"type":"object","properties":{"id":{"type":"integer"},"amount":{"type":"number"},"date":{"type":"string"},"note":{"type":"string"}},"required":["id","amount"],"additionalProperties":false}"""),
        new("propose_delete_contribution",
            "Propose deleting a goal contribution by id. Requires Approve.",
            """{"type":"object","properties":{"contribution_id":{"type":"integer"}},"required":["contribution_id"],"additionalProperties":false}"""),
        new("propose_set_aside_monthly",
            "Propose recording this month's sinking-fund set-aside. Requires Approve.",
            """{"type":"object","properties":{"id":{"type":"integer"}},"required":["id"],"additionalProperties":false}"""),
        new("propose_create_job",
            "Propose creating an hourly job for paycheck estimates. Requires Approve. Rate is dollars/hour.",
            JobSchema(required: ["name", "hourly_rate", "rate_effective_date", "pay_frequency", "pay_period_anchor"])),
        new("propose_update_job",
            "Propose updating a job by id. Requires Approve.",
            JobSchema(required: ["id", "name", "hourly_rate", "rate_effective_date", "pay_frequency", "pay_period_anchor"])),
        new("propose_archive_job",
            "Propose archiving or restoring a job. Requires Approve.",
            """{"type":"object","properties":{"id":{"type":"integer"},"archived":{"type":"boolean"}},"required":["id","archived"],"additionalProperties":false}"""),
        new("propose_add_shift",
            "Propose adding a shift. Prefer scheduled_minutes, or start_time and end_time (HH:mm). Requires Approve.",
            """{"type":"object","properties":{"job_id":{"type":"integer"},"date":{"type":"string"},"scheduled_minutes":{"type":"integer","minimum":1},"start_time":{"type":"string"},"end_time":{"type":"string"},"break_override_minutes":{"type":"integer"},"status":{"type":"string","enum":["scheduled","worked"]},"note":{"type":"string"}},"required":["job_id","date"],"additionalProperties":false}"""),
        new("propose_update_shift",
            "Propose updating a shift by id. Requires Approve.",
            """{"type":"object","properties":{"id":{"type":"integer"},"job_id":{"type":"integer"},"date":{"type":"string"},"scheduled_minutes":{"type":"integer","minimum":1},"start_time":{"type":"string"},"end_time":{"type":"string"},"break_override_minutes":{"type":"integer"},"status":{"type":"string","enum":["scheduled","worked"]},"note":{"type":"string"}},"required":["id","job_id","date"],"additionalProperties":false}"""),
        new("propose_delete_shift",
            "Propose deleting a shift. Requires Approve.",
            """{"type":"object","properties":{"id":{"type":"integer"}},"required":["id"],"additionalProperties":false}"""),
        new("propose_set_home_tax",
            "Propose home tax settings used for paycheck estimates. Requires Approve.",
            """{"type":"object","properties":{"location":{"type":"string"},"psd":{"type":"string"},"resident_eit_percent":{"type":"number","minimum":0},"in_pennsylvania":{"type":"boolean"}},"required":["location","resident_eit_percent"],"additionalProperties":false}"""),
        new("propose_save_deductions",
            "Propose replacing a job's current deductions list. Amounts are dollars; percent is e.g. 5 for 5%. Requires Approve.",
            """{"type":"object","properties":{"job_id":{"type":"integer"},"deductions":{"type":"array","items":{"type":"object","properties":{"id":{"type":"integer"},"name":{"type":"string"},"kind":{"type":"string","enum":["pre_tax_health","pre_tax_retirement","after_tax"]},"amount":{"type":"number","minimum":0},"percent":{"type":"number","minimum":0},"start_date":{"type":"string"}},"required":["name","kind","start_date"],"additionalProperties":false}}},"required":["job_id","deductions"],"additionalProperties":false}"""),
        new("propose_save_paystub",
            "Propose saving a paystub for a job pay period. Amounts are dollars. Requires Approve.",
            """{"type":"object","properties":{"job_id":{"type":"integer"},"period_start":{"type":"string"},"pay_date":{"type":"string"},"gross":{"type":"number"},"net":{"type":"number"},"paid_minutes":{"type":"integer"},"federal":{"type":"number"},"social_security":{"type":"number"},"medicare":{"type":"number"},"state":{"type":"number"},"state_unemployment":{"type":"number"},"local_eit":{"type":"number"},"local_services_tax":{"type":"number"}},"required":["job_id","period_start","pay_date","gross","net"],"additionalProperties":false}"""),
        new("propose_delete_paystub",
            "Propose deleting a paystub. Requires Approve.",
            """{"type":"object","properties":{"job_id":{"type":"integer"},"period_start":{"type":"string"}},"required":["job_id","period_start"],"additionalProperties":false}"""),
    ];

    private static string JobSchema(string[] required)
    {
        var req = string.Join(",", required.Select(r => $"\"{r}\""));
        return $$$"""{"type":"object","properties":{"id":{"type":"integer"},"name":{"type":"string"},"hourly_rate":{"type":"number","exclusiveMinimum":0},"rate_effective_date":{"type":"string"},"pay_frequency":{"type":"string","enum":["weekly","biweekly","semimonthly","monthly"]},"pay_period_anchor":{"type":"string"},"payday_offset_days":{"type":"integer"},"payday_confirmed":{"type":"boolean"},"workweek_start":{"type":"string","enum":["sunday","monday","tuesday","wednesday","thursday","friday","saturday"]},"overtime_eligible":{"type":"boolean"},"deposit_account":{"type":"string"},"income_category":{"type":"string"},"filing_status":{"type":"string","enum":["single","married_joint","head_of_household"]},"w4_multiple_jobs":{"type":"boolean"},"w4_dependents_credit":{"type":"number","minimum":0},"w4_other_income":{"type":"number","minimum":0},"w4_deductions":{"type":"number","minimum":0},"w4_extra_withholding":{"type":"number","minimum":0},"work_location":{"type":"string"},"work_psd":{"type":"string"},"work_non_resident_eit_percent":{"type":"number","minimum":0},"work_lst_annual":{"type":"number","minimum":0},"lst_exempt":{"type":"boolean"},"unpaid_break_minutes":{"type":"integer"},"unpaid_break_after_minutes":{"type":"integer"},"paid_break_minutes":{"type":"integer"}},"required":[{{{req}}}],"additionalProperties":false}""";
    }

    /// <summary>Parses a propose_* tool into a kind, human summary and payload. Throws on bad input.</summary>
    public async Task<(MutationKind Kind, string Summary, string PayloadJson)> ParseAsync(string toolName, JsonElement input, CancellationToken ct = default)
    {
        return toolName switch
        {
            "propose_create_transaction" => await TxAsync(MutationKind.CreateTransaction, null, input, ct),
            "propose_update_transaction" => await TxAsync(MutationKind.UpdateTransaction, NeedInt(input, "id"), input, ct),
            "propose_delete_transaction" => (MutationKind.DeleteTransaction, $"Delete transaction #{NeedInt(input, "id")}", Compact(input)),
            "propose_restore_transaction" => (MutationKind.RestoreTransaction, $"Restore transaction #{NeedInt(input, "id")}", Compact(input)),
            "propose_create_account" => (MutationKind.CreateAccount, $"Create account \"{NeedStr(input, "name")}\"", Compact(input)),
            "propose_update_account" => (MutationKind.UpdateAccount, $"Update account #{NeedInt(input, "id")}", Compact(input)),
            "propose_archive_account" => (MutationKind.ArchiveAccount, $"{(Bool(input, "archived") == true ? "Archive" : "Restore")} account #{NeedInt(input, "id")}", Compact(input)),
            "propose_set_bank_balance" => (MutationKind.SetBankBalance, $"Set bank balance for account #{NeedInt(input, "id")} on {NeedStr(input, "date")}", Compact(input)),
            "propose_create_category" => (MutationKind.CreateCategory, $"Create category \"{NeedStr(input, "name")}\"", Compact(input)),
            "propose_rename_category" => (MutationKind.RenameCategory, $"Rename category #{NeedInt(input, "id")} to \"{NeedStr(input, "name")}\"", Compact(input)),
            "propose_move_category" => (MutationKind.MoveCategory, $"Move category #{NeedInt(input, "id")}", Compact(input)),
            "propose_archive_category" => (MutationKind.ArchiveCategory, $"{(Bool(input, "archived") == true ? "Archive" : "Restore")} category #{NeedInt(input, "id")}", Compact(input)),
            "propose_set_budget" => (MutationKind.SetBudget, $"Set budget for {Str(input, "group")} to ${NeedNumber(input, "planned"):0.##}", Compact(input)),
            "propose_copy_budget" => (MutationKind.CopyBudget, $"Copy previous budget into {Str(input, "month") ?? "this month"}", Compact(input)),
            "propose_create_recurring" => (MutationKind.CreateRecurring, $"Create recurring \"{NeedStr(input, "name")}\"", Compact(input)),
            "propose_update_recurring" => (MutationKind.UpdateRecurring, $"Update recurring #{NeedInt(input, "id")}", Compact(input)),
            "propose_set_recurring_active" => (MutationKind.SetRecurringActive, $"{(Bool(input, "active") == true ? "Resume" : "Pause")} recurring #{NeedInt(input, "id")}", Compact(input)),
            "propose_delete_recurring" => (MutationKind.DeleteRecurring, $"Delete recurring #{NeedInt(input, "id")}", Compact(input)),
            "propose_skip_recurring" => (MutationKind.SkipRecurring, $"Skip recurring #{NeedInt(input, "id")} on {NeedStr(input, "date")}", Compact(input)),
            "propose_unskip_recurring" => (MutationKind.UnskipRecurring, $"Unskip recurring #{NeedInt(input, "id")} on {NeedStr(input, "date")}", Compact(input)),
            "propose_record_recurring" => (MutationKind.RecordRecurring, $"Record recurring #{NeedInt(input, "id")} on {NeedStr(input, "date")}", Compact(input)),
            "propose_create_goal" => (MutationKind.CreateGoal, $"Create goal \"{NeedStr(input, "name")}\"", Compact(input)),
            "propose_update_goal" => (MutationKind.UpdateGoal, $"Update goal #{NeedInt(input, "id")}", Compact(input)),
            "propose_archive_goal" => (MutationKind.ArchiveGoal, $"{(Bool(input, "archived") == true ? "Archive" : "Restore")} goal #{NeedInt(input, "id")}", Compact(input)),
            "propose_delete_goal" => (MutationKind.DeleteGoal, $"Delete goal #{NeedInt(input, "id")}", Compact(input)),
            "propose_contribute_goal" => (MutationKind.ContributeGoal, $"Contribute ${NeedNumber(input, "amount"):0.##} to goal #{NeedInt(input, "id")}", Compact(input)),
            "propose_delete_contribution" => (MutationKind.DeleteContribution, $"Delete contribution #{NeedInt(input, "contribution_id")}", Compact(input)),
            "propose_set_aside_monthly" => (MutationKind.SetAsideMonthly, $"Set aside monthly for goal #{NeedInt(input, "id")}", Compact(input)),
            "propose_create_job" => (MutationKind.CreateJob, $"Create job \"{NeedStr(input, "name")}\"", Compact(input)),
            "propose_update_job" => (MutationKind.UpdateJob, $"Update job #{NeedInt(input, "id")}", Compact(input)),
            "propose_archive_job" => (MutationKind.ArchiveJob, $"{(Bool(input, "archived") == true ? "Archive" : "Restore")} job #{NeedInt(input, "id")}", Compact(input)),
            "propose_add_shift" => (MutationKind.AddShift, $"Add shift for job #{NeedInt(input, "job_id")} on {NeedStr(input, "date")}", Compact(input)),
            "propose_update_shift" => (MutationKind.UpdateShift, $"Update shift #{NeedInt(input, "id")}", Compact(input)),
            "propose_delete_shift" => (MutationKind.DeleteShift, $"Delete shift #{NeedInt(input, "id")}", Compact(input)),
            "propose_set_home_tax" => (MutationKind.SetHomeTax, $"Set home tax for \"{NeedStr(input, "location")}\"", Compact(input)),
            "propose_save_deductions" => (MutationKind.SaveDeductions, $"Save deductions for job #{NeedInt(input, "job_id")}", Compact(input)),
            "propose_save_paystub" => (MutationKind.SavePaystub, $"Save paystub for job #{NeedInt(input, "job_id")}", Compact(input)),
            "propose_delete_paystub" => (MutationKind.DeletePaystub, $"Delete paystub for job #{NeedInt(input, "job_id")}", Compact(input)),
            _ => throw new LedgerValidationException($"There is no write tool called {toolName}."),
        };
    }

    /// <summary>Applies an approved proposal. Returns a JSON result object.</summary>
    public async Task<string> ApplyAsync(MutationKind kind, string payloadJson, CancellationToken ct = default)
    {
        var input = JsonDocument.Parse(payloadJson).RootElement;
        try
        {
            JsonNode result = kind switch
            {
                MutationKind.CreateTransaction => await CreateTxAsync(input, ct),
                MutationKind.UpdateTransaction => await UpdateTxAsync(input, ct),
                MutationKind.DeleteTransaction => await DoAsync(async () => { await transactions.DeleteAsync(NeedInt(input, "id"), ct); return Ok(); }),
                MutationKind.RestoreTransaction => await DoAsync(async () => { await transactions.RestoreAsync(NeedInt(input, "id"), ct); return Ok(); }),
                MutationKind.CreateAccount => IdNode((await accounts.CreateAsync(await AccountInputAsync(input, ct), ct)).Id),
                MutationKind.UpdateAccount => await DoAsync(async () => { await accounts.UpdateAsync(NeedInt(input, "id"), await AccountInputAsync(input, ct), ct); return Ok(); }),
                MutationKind.ArchiveAccount => await DoAsync(async () => { await accounts.SetArchivedAsync(NeedInt(input, "id"), NeedBool(input, "archived"), ct); return Ok(); }),
                MutationKind.SetBankBalance => await DoAsync(async () =>
                {
                    await accounts.SetBalanceFromBankAsync(NeedInt(input, "id"), NeedDate(input, "date"), NeedCents(input, "balance"), ct);
                    return Ok();
                }),
                MutationKind.CreateCategory => IdNode((await categories.CreateAsync(NeedStr(input, "name"), await ParentIdAsync(Str(input, "parent"), ct), CategoryKind(NeedStr(input, "type")), ct)).Id),
                MutationKind.RenameCategory => await DoAsync(async () => { await categories.RenameAsync(NeedInt(input, "id"), NeedStr(input, "name"), ct); return Ok(); }),
                MutationKind.MoveCategory => await DoAsync(async () => { await categories.MoveAsync(NeedInt(input, "id"), await ParentIdAsync(Str(input, "parent"), ct), ct); return Ok(); }),
                MutationKind.ArchiveCategory => await DoAsync(async () => { await categories.SetArchivedAsync(NeedInt(input, "id"), NeedBool(input, "archived"), ct); return Ok(); }),
                MutationKind.SetBudget => await SetBudgetAsync(input, ct),
                MutationKind.CopyBudget => await CopyBudgetAsync(input, ct),
                MutationKind.CreateRecurring => IdNode((await recurring.CreateAsync(await RecurringInputAsync(input, ct), ct)).Id),
                MutationKind.UpdateRecurring => await DoAsync(async () => { await recurring.UpdateAsync(NeedInt(input, "id"), await RecurringInputAsync(input, ct), ct); return Ok(); }),
                MutationKind.SetRecurringActive => await DoAsync(async () => { await recurring.SetActiveAsync(NeedInt(input, "id"), NeedBool(input, "active"), ct); return Ok(); }),
                MutationKind.DeleteRecurring => await DoAsync(async () => { await recurring.DeleteAsync(NeedInt(input, "id"), ct); return Ok(); }),
                MutationKind.SkipRecurring => await DoAsync(async () => { await recurring.SkipAsync(NeedInt(input, "id"), NeedDate(input, "date"), ct); return Ok(); }),
                MutationKind.UnskipRecurring => await DoAsync(async () => { await recurring.UnskipAsync(NeedInt(input, "id"), NeedDate(input, "date"), ct); return Ok(); }),
                MutationKind.RecordRecurring => await DoAsync(async () =>
                {
                    await recurring.RecordAsync(NeedInt(input, "id"), NeedDate(input, "date"), NeedInt(input, "transaction_id"), ct);
                    return Ok();
                }),
                MutationKind.CreateGoal => IdNode((await goals.CreateAsync(await GoalInputAsync(input, ct), ct)).Id),
                MutationKind.UpdateGoal => await DoAsync(async () => { await goals.UpdateAsync(NeedInt(input, "id"), await GoalInputAsync(input, ct), ct); return Ok(); }),
                MutationKind.ArchiveGoal => await DoAsync(async () => { await goals.SetArchivedAsync(NeedInt(input, "id"), NeedBool(input, "archived"), ct); return Ok(); }),
                MutationKind.DeleteGoal => await DoAsync(async () => { await goals.DeleteAsync(NeedInt(input, "id"), ct); return Ok(); }),
                MutationKind.ContributeGoal => IdNode((await goals.AddContributionAsync(NeedInt(input, "id"), SignedCents(input, "amount"), Date(input, "date") ?? Today, Str(input, "note"), ct)).Id),
                MutationKind.DeleteContribution => await DoAsync(async () => { await goals.DeleteContributionAsync(NeedInt(input, "contribution_id"), ct); return Ok(); }),
                MutationKind.SetAsideMonthly => await DoAsync(async () => { await goals.SetAsideMonthlyAsync(NeedInt(input, "id"), ct); return Ok(); }),
                MutationKind.CreateJob => IdNode((await income.CreateJobAsync(await JobInputAsync(input, ct), ct)).Id),
                MutationKind.UpdateJob => await DoAsync(async () => { await income.UpdateJobAsync(NeedInt(input, "id"), await JobInputAsync(input, ct), ct); return Ok(); }),
                MutationKind.ArchiveJob => await DoAsync(async () => { await income.SetJobArchivedAsync(NeedInt(input, "id"), NeedBool(input, "archived"), ct); return Ok(); }),
                MutationKind.AddShift => IdNode((await income.AddShiftAsync(await ShiftInputAsync(input, ct), ct)).Id),
                MutationKind.UpdateShift => await DoAsync(async () => { await income.UpdateShiftAsync(NeedInt(input, "id"), await ShiftInputAsync(input, ct), ct); return Ok(); }),
                MutationKind.DeleteShift => await DoAsync(async () => { await income.DeleteShiftAsync(NeedInt(input, "id"), ct); return Ok(); }),
                MutationKind.SetHomeTax => await DoAsync(async () =>
                {
                    await income.SetHomeTaxSettingsAsync(new HomeTaxSettings(
                        NeedStr(input, "location"),
                        Str(input, "psd"),
                        (int)Math.Round(NeedNumber(input, "resident_eit_percent") * 100),
                        Bool(input, "in_pennsylvania") ?? true), ct);
                    return Ok();
                }),
                MutationKind.SaveDeductions => await SaveDeductionsAsync(input, ct),
                MutationKind.SavePaystub => await SavePaystubAsync(input, ct),
                MutationKind.DeletePaystub => await DoAsync(async () =>
                {
                    await paystubs.DeleteAsync(NeedInt(input, "job_id"), NeedDate(input, "period_start"), ct);
                    return Ok();
                }),
                _ => throw new LedgerValidationException($"Unknown mutation {kind}."),
            };
            return result.ToJsonString();
        }
        catch (LedgerValidationException)
        {
            throw;
        }
    }

    private async Task<(MutationKind, string, string)> TxAsync(MutationKind kind, int? id, JsonElement input, CancellationToken ct)
    {
        var amount = NeedNumber(input, "amount");
        var account = NeedStr(input, "account");
        var payee = Str(input, "payee");
        var label = id is { } i ? $"Update transaction #{i}" : "Add transaction";
        var summary = $"{label}: {NeedStr(input, "kind")} ${amount:0.##} at {account}" + (payee is { } p ? $" ({p})" : "");
        _ = await AccountIdAsync(account, ct); // validate early
        return (kind, summary, Compact(input));
    }

    private async Task<JsonNode> CreateTxAsync(JsonElement input, CancellationToken ct) =>
        IdNode((await transactions.CreateAsync(await TxInputAsync(input, ct), ct)).Id);

    private async Task<JsonNode> UpdateTxAsync(JsonElement input, CancellationToken ct)
    {
        await transactions.UpdateAsync(NeedInt(input, "id"), await TxInputAsync(input, ct), ct);
        return Ok();
    }

    private async Task<TransactionInput> TxInputAsync(JsonElement input, CancellationToken ct) => new(
        TxKind(NeedStr(input, "kind")),
        NeedCents(input, "amount"),
        Date(input, "date") ?? Today,
        await AccountIdAsync(NeedStr(input, "account"), ct),
        Str(input, "transfer_account") is { } ta ? await AccountIdAsync(ta, ct) : null,
        Str(input, "category") is { } c ? await CategoryIdAsync(c, ct) : null,
        Str(input, "payee"),
        Str(input, "note"),
        Bool(input, "pending") == true ? TransactionStatus.Pending : TransactionStatus.Cleared);

    private async Task<AccountInput> AccountInputAsync(JsonElement input, CancellationToken ct) => new(
        NeedStr(input, "name"),
        AccountKind(NeedStr(input, "type")),
        Cents(input, "starting_balance") ?? 0,
        Str(input, "institution"),
        Bool(input, "include_in_net_worth") ?? true,
        Date(input, "balance_date"));

    private async Task<RecurringInput> RecurringInputAsync(JsonElement input, CancellationToken ct) => new(
        NeedStr(input, "name"),
        TxKind(NeedStr(input, "kind")),
        NeedCents(input, "amount"),
        Frequency(NeedStr(input, "frequency")),
        NeedDate(input, "start_date"),
        await AccountIdAsync(NeedStr(input, "account"), ct),
        Date(input, "end_date"),
        Str(input, "transfer_account") is { } ta ? await AccountIdAsync(ta, ct) : null,
        Str(input, "category") is { } c ? await CategoryIdAsync(c, ct) : null,
        Bool(input, "estimated") ?? false,
        Str(input, "note"),
        Bool(input, "auto_record") ?? false);

    private async Task<GoalInput> GoalInputAsync(JsonElement input, CancellationToken ct) => new(
        NeedStr(input, "name"),
        NeedCents(input, "target"),
        Date(input, "target_date"),
        Str(input, "tracking") == "account" ? GoalTracking.AccountBalance : GoalTracking.Manual,
        Cents(input, "starting") ?? 0,
        Str(input, "account") is { } a ? await AccountIdAsync(a, ct) : null,
        Bool(input, "sinking_fund") ?? false,
        Cents(input, "monthly_set_aside"),
        Str(input, "category") is { } c ? await CategoryIdAsync(c, ct) : null);

    private async Task<JobInput> JobInputAsync(JsonElement input, CancellationToken ct) => new(
        NeedStr(input, "name"),
        NeedCents(input, "hourly_rate"),
        NeedDate(input, "rate_effective_date"),
        PayFreq(NeedStr(input, "pay_frequency")),
        NeedDate(input, "pay_period_anchor"),
        Int(input, "payday_offset_days") ?? 0,
        Bool(input, "payday_confirmed") ?? false,
        Weekday(Str(input, "workweek_start") ?? "sunday"),
        Bool(input, "overtime_eligible") ?? true,
        Str(input, "deposit_account") is { } da ? await AccountIdAsync(da, ct) : null,
        Str(input, "income_category") is { } ic ? await CategoryIdAsync(ic, ct) : null,
        Filing(Str(input, "filing_status") ?? "single"),
        Bool(input, "w4_multiple_jobs") ?? false,
        Cents(input, "w4_dependents_credit") ?? 0,
        Cents(input, "w4_other_income") ?? 0,
        Cents(input, "w4_deductions") ?? 0,
        Cents(input, "w4_extra_withholding") ?? 0,
        Str(input, "work_location"),
        Str(input, "work_psd"),
        Number(input, "work_non_resident_eit_percent") is { } nr ? (int)Math.Round(nr * 100) : 0,
        Cents(input, "work_lst_annual") ?? 0,
        Bool(input, "lst_exempt") ?? false,
        Int(input, "unpaid_break_minutes") ?? 30,
        Int(input, "unpaid_break_after_minutes") ?? 300,
        Int(input, "paid_break_minutes") ?? 15);

    private Task<ShiftInput> ShiftInputAsync(JsonElement input, CancellationToken ct) => Task.FromResult(new ShiftInput(
        NeedInt(input, "job_id"),
        NeedDate(input, "date"),
        Int(input, "scheduled_minutes"),
        Time(input, "start_time"),
        Time(input, "end_time"),
        Int(input, "break_override_minutes"),
        ShiftState(Str(input, "status") ?? "scheduled"),
        Str(input, "note")));

    private async Task<JsonNode> SetBudgetAsync(JsonElement input, CancellationToken ct)
    {
        var (year, month) = Month(input, "month") ?? (Today.Year, Today.Month);
        var group = NeedStr(input, "group");
        var tree = await categories.GetTreeAsync(ct: ct);
        var cat = tree.Select(n => n.Category).FirstOrDefault(c => string.Equals(c.Name, group, StringComparison.OrdinalIgnoreCase) && c.ParentCategoryId is null)
            ?? throw new LedgerValidationException($"There's no spending group called \"{group}\".");
        await budgets.SetPlannedAsync(year, month, cat.Id, NeedCents(input, "planned"), ct);
        return Ok();
    }

    private async Task<JsonNode> CopyBudgetAsync(JsonElement input, CancellationToken ct)
    {
        var (year, month) = Month(input, "month") ?? (Today.Year, Today.Month);
        var copied = await budgets.CopyPreviousPlanAsync(year, month, ct);
        return new JsonObject { ["ok"] = true, ["lines_copied"] = copied };
    }

    private async Task<JsonNode> SaveDeductionsAsync(JsonElement input, CancellationToken ct)
    {
        if (!input.TryGetProperty("deductions", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            throw new LedgerValidationException("deductions must be an array.");
        }
        var items = new List<DeductionInput>();
        foreach (var d in list.EnumerateArray())
        {
            items.Add(new DeductionInput(
                Int(d, "id"),
                NeedStr(d, "name"),
                Deduction(NeedStr(d, "kind")),
                Cents(d, "amount") ?? 0,
                Number(d, "percent") is { } p ? (int)Math.Round(p * 100) : null,
                NeedDate(d, "start_date")));
        }
        await deductions.SaveAsync(NeedInt(input, "job_id"), items, Today, ct);
        return Ok();
    }

    private async Task<JsonNode> SavePaystubAsync(JsonElement input, CancellationToken ct)
    {
        var stub = await paystubs.SaveAsync(NeedInt(input, "job_id"), NeedDate(input, "period_start"), new PaystubInput(
            NeedDate(input, "pay_date"),
            NeedCents(input, "gross"),
            NeedCents(input, "net"),
            Int(input, "paid_minutes"),
            null,
            null,
            Cents(input, "federal"),
            Cents(input, "social_security"),
            Cents(input, "medicare"),
            Cents(input, "state"),
            Cents(input, "state_unemployment"),
            Cents(input, "local_eit"),
            Cents(input, "local_services_tax")), ct);
        return IdNode(stub.Id);
    }

    private async Task<int> AccountIdAsync(string name, CancellationToken ct)
    {
        var open = (await accounts.GetAccountBalancesAsync(ct: ct)).Select(b => b.Account).ToList();
        return open.FirstOrDefault(a => string.Equals(a.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))?.Id
            ?? throw new LedgerValidationException($"There's no open account called \"{name}\". Accounts: {string.Join(", ", open.Select(a => a.Name))}.");
    }

    private async Task<int> CategoryIdAsync(string name, CancellationToken ct)
    {
        var all = (await categories.GetTreeAsync(includeArchived: true, ct: ct)).SelectMany(n => n.Children.Prepend(n.Category)).ToList();
        return all.FirstOrDefault(c => string.Equals(c.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))?.Id
            ?? throw new LedgerValidationException($"There's no category called \"{name}\".");
    }

    private async Task<int?> ParentIdAsync(string? name, CancellationToken ct) =>
        name is null ? null : await CategoryIdAsync(name, ct);

    private static async Task<JsonNode> DoAsync(Func<Task<JsonNode>> action) => await action();

    private static JsonObject Ok() => new() { ["ok"] = true };

    private static JsonObject IdNode(int id) => new() { ["ok"] = true, ["id"] = id };

    private static string Compact(JsonElement e) => e.GetRawText();

    private static TransactionType TxKind(string s) => s switch
    {
        "spending" => TransactionType.Expense,
        "income" => TransactionType.Income,
        "transfer" => TransactionType.Transfer,
        "refund" => TransactionType.Refund,
        _ => throw new LedgerValidationException($"Unknown kind \"{s}\"."),
    };

    private static AccountType AccountKind(string s) => s switch
    {
        "checking" => AccountType.Checking,
        "savings" => AccountType.Savings,
        "credit_card" => AccountType.CreditCard,
        "cash" => AccountType.Cash,
        "other" => AccountType.Other,
        _ => throw new LedgerValidationException($"Unknown account type \"{s}\"."),
    };

    private static CategoryType CategoryKind(string s) => s switch
    {
        "spending" => CategoryType.Expense,
        "income" => CategoryType.Income,
        "savings" => CategoryType.Savings,
        _ => throw new LedgerValidationException($"Unknown category type \"{s}\"."),
    };

    private static RecurrenceFrequency Frequency(string s) => s switch
    {
        "weekly" => RecurrenceFrequency.Weekly,
        "biweekly" => RecurrenceFrequency.Biweekly,
        "monthly" => RecurrenceFrequency.Monthly,
        "quarterly" => RecurrenceFrequency.Quarterly,
        "yearly" => RecurrenceFrequency.Yearly,
        _ => throw new LedgerValidationException($"Unknown frequency \"{s}\"."),
    };

    private static PayFrequency PayFreq(string s) => s switch
    {
        "weekly" => PayFrequency.Weekly,
        "biweekly" => PayFrequency.Biweekly,
        "semimonthly" => PayFrequency.Semimonthly,
        "monthly" => PayFrequency.Monthly,
        _ => throw new LedgerValidationException($"Unknown pay frequency \"{s}\"."),
    };

    private static FilingStatus Filing(string s) => s switch
    {
        "single" => FilingStatus.Single,
        "married_joint" => FilingStatus.MarriedFilingJointly,
        "head_of_household" => FilingStatus.HeadOfHousehold,
        _ => throw new LedgerValidationException($"Unknown filing status \"{s}\"."),
    };

    private static DayOfWeek Weekday(string s) => s switch
    {
        "sunday" => DayOfWeek.Sunday,
        "monday" => DayOfWeek.Monday,
        "tuesday" => DayOfWeek.Tuesday,
        "wednesday" => DayOfWeek.Wednesday,
        "thursday" => DayOfWeek.Thursday,
        "friday" => DayOfWeek.Friday,
        "saturday" => DayOfWeek.Saturday,
        _ => throw new LedgerValidationException($"Unknown weekday \"{s}\"."),
    };

    private static ShiftStatus ShiftState(string s) => s switch
    {
        "scheduled" => ShiftStatus.Scheduled,
        "worked" => ShiftStatus.Worked,
        _ => throw new LedgerValidationException($"Unknown shift status \"{s}\"."),
    };

    private static DeductionKind Deduction(string s) => s switch
    {
        "pre_tax_health" => DeductionKind.PreTaxHealth,
        "pre_tax_retirement" => DeductionKind.PreTaxRetirement,
        "after_tax" => DeductionKind.AfterTax,
        _ => throw new LedgerValidationException($"Unknown deduction kind \"{s}\"."),
    };

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    private static string NeedStr(JsonElement e, string name) => Str(e, name) ?? throw new LedgerValidationException($"{name} is required.");

    private static decimal? Number(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;

    private static decimal NeedNumber(JsonElement e, string name) => Number(e, name) ?? throw new LedgerValidationException($"{name} is required.");

    private static int? Int(JsonElement e, string name) => Number(e, name) is { } d ? (int)Math.Round(d) : null;

    private static int NeedInt(JsonElement e, string name) => Int(e, name) ?? throw new LedgerValidationException($"{name} is required.");

    private static bool? Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && (v.ValueKind is JsonValueKind.True or JsonValueKind.False) ? v.GetBoolean() : null;

    private static bool NeedBool(JsonElement e, string name) => Bool(e, name) ?? throw new LedgerValidationException($"{name} is required.");

    private static long? Cents(JsonElement e, string name) => Number(e, name) is { } d ? (long)Math.Round(d * 100, MidpointRounding.AwayFromZero) : null;

    private static long NeedCents(JsonElement e, string name) => Cents(e, name) ?? throw new LedgerValidationException($"{name} is required.");

    private static long SignedCents(JsonElement e, string name) => Number(e, name) is { } d
        ? (long)Math.Round(d * 100, MidpointRounding.AwayFromZero)
        : throw new LedgerValidationException($"{name} is required.");

    private static DateOnly? Date(JsonElement e, string name) => Str(e, name) is not { } s
        ? null
        : DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : throw new LedgerValidationException($"{name} must be a date like 2026-10-15.");

    private static DateOnly NeedDate(JsonElement e, string name) => Date(e, name) ?? throw new LedgerValidationException($"{name} is required.");

    private static TimeOnly? Time(JsonElement e, string name) => Str(e, name) is not { } s
        ? null
        : TimeOnly.TryParseExact(s, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t
            : throw new LedgerValidationException($"{name} must look like 09:30.");

    private static (int Year, int Month)? Month(JsonElement e, string name) => Str(e, name) is not { } s
        ? null
        : DateOnly.TryParseExact(s + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? (d.Year, d.Month)
            : throw new LedgerValidationException($"{name} must look like 2026-10.");

}

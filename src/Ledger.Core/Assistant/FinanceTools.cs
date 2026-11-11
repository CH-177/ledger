using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ledger.Core.Forecasting;
using Ledger.Core.Models;
using Ledger.Core.Services;

namespace Ledger.Core.Assistant;

/// <summary>A tool the assistant may call: its name, what it's for, and its input as a JSON Schema object.</summary>
public sealed record AssistantTool(string Name, string Description, string InputSchemaJson);

/// <summary>
/// The assistant's window onto the user's finances: read-only questions answered with Ledger's own
/// calculations (the same ones the pages use), returned as JSON. Nothing here can change data, and
/// nothing identifies an account beyond the name the user gave it (Ledger stores no account
/// numbers; the bank ids remembered for imports are never included). Amounts are dollars.
/// </summary>
public sealed class FinanceTools(
    AccountService accounts,
    TransactionService transactions,
    BudgetService budgets,
    DashboardService dashboard,
    ForecastService forecasts,
    RecurringService recurring,
    IncomeForecastService income,
    IncomeService jobs,
    GoalService goals,
    ScenarioService scenarios,
    CategoryService categories,
    TimeProvider time)
{
    public const int MaxTransactions = 150;
    public const int MaxForecastDays = 180;

    private DateOnly Today => DateOnly.FromDateTime(time.GetLocalNow().Date);

    public static IReadOnlyList<AssistantTool> Definitions { get; } =
    [
        new("get_overview",
            "Where the user stands today: each account's balance, money available (all open accounts except credit cards), " +
            "money set aside in sinking funds and what's free to spend, card debt, net worth, this month's income/spending/saving " +
            "so far, and the next 14 days' expected money in and out with the lowest projected balance. Start here for most questions.",
            """{"type":"object","properties":{},"additionalProperties":false}"""),

        new("get_budget",
            "The monthly budget for one month: each spending group's planned amount, what was actually spent, what's left, and " +
            "its status. Spending in subcategories rolls up into its group; refunds lower spending.",
            """{"type":"object","properties":{"month":{"type":"string","description":"YYYY-MM. Defaults to the current month."}},"additionalProperties":false}"""),

        new("get_spending_history",
            "Actual income and spending for each of the last N months (the current month is partial), with spending broken " +
            "down by spending group and each group's monthly average over the complete months. Use this to suggest realistic budgets.",
            """{"type":"object","properties":{"months":{"type":"integer","minimum":1,"maximum":12,"description":"How many months back, including the current one. Default 4."}},"additionalProperties":false}"""),

        new("search_transactions",
            $"Individual transactions, newest first (at most {MaxTransactions}), with totals for everything that matched. " +
            "Filter by date range, text (matches payee, note, category or amount), category name, and kind.",
            """{"type":"object","properties":{"from":{"type":"string","description":"YYYY-MM-DD"},"to":{"type":"string","description":"YYYY-MM-DD"},"search":{"type":"string"},"category":{"type":"string","description":"A category or group name, e.g. Groceries or Food."},"kind":{"type":"string","enum":["spending","income","transfer","refund"]},"limit":{"type":"integer","minimum":1,"maximum":150}},"additionalProperties":false}"""),

        new("get_upcoming",
            "The day-by-day forecast from today's real balance: bills and income not yet recorded, future-dated transactions, " +
            "and estimated paychecks, each with the projected balance after it. Covers open accounts except credit cards.",
            """{"type":"object","properties":{"days":{"type":"integer","minimum":1,"maximum":180,"description":"Default 30."}},"additionalProperties":false}"""),

        new("get_recurring",
            "Every recurring bill, subscription, income and transfer: amount, how often, the monthly equivalent, the next date, " +
            "and whether the amount is an estimate or the item is paused.",
            """{"type":"object","properties":{},"additionalProperties":false}"""),

        new("get_paycheck_estimates",
            "Predicted paychecks from the user's hourly job(s) and scheduled shifts: pay date, hours, gross, take-home after taxes, " +
            "and whether the real paycheck has been recorded yet. Estimates are not money the user has.",
            """{"type":"object","properties":{"days":{"type":"integer","minimum":1,"maximum":120,"description":"How far ahead. Default 45."}},"additionalProperties":false}"""),

        new("get_goals",
            "Savings goals and sinking funds: target, amount saved, target date, suggested monthly amount, and for sinking funds " +
            "the monthly set-aside and the spending category they pay for.",
            """{"type":"object","properties":{},"additionalProperties":false}"""),

        new("can_i_afford",
            "Runs Ledger's affordability check: adds a one-off purchase to the forecast and looks 60 days past it. Returns whether " +
            "the balance stays at or above zero (and the cushion, if given), the lowest point with and without it, and a plain answer. " +
            "Always use this for 'can I afford X' questions instead of estimating.",
            """{"type":"object","properties":{"amount":{"type":"number","exclusiveMinimum":0,"description":"Dollars."},"date":{"type":"string","description":"YYYY-MM-DD, today or later. Default today."},"account":{"type":"string","description":"Account name it's paid from. Default: the main checking account."},"cushion":{"type":"number","minimum":0,"description":"Dollars the user wants to keep as a minimum. Default 0."}},"required":["amount"],"additionalProperties":false}"""),

        new("what_if",
            "Compares the forecast as things are with the forecast under one or more hypothetical changes (nothing is saved): " +
            "a purchase, extra money in, a new recurring bill or income, stopping a recurring item, working a percentage of " +
            "scheduled hours, a set number of paid hours a week, or a new hourly pay rate. Paycheck changes are re-priced with taxes.",
            """{"type":"object","properties":{"days":{"type":"integer","minimum":7,"maximum":180,"description":"How far ahead to compare. Default 90."},"changes":{"type":"array","minItems":1,"maxItems":10,"items":{"type":"object","properties":{"kind":{"type":"string","enum":["purchase","extra_income","new_bill","new_income","stop_recurring","hours_percent","weekly_hours","pay_rate"]},"date":{"type":"string","description":"YYYY-MM-DD the change starts. Default today."},"amount":{"type":"number","description":"Dollars: the purchase, money in, bill or income amount, or the new hourly rate."},"frequency":{"type":"string","enum":["weekly","biweekly","monthly","quarterly","yearly"],"description":"For new_bill and new_income. Default monthly."},"percent":{"type":"integer","minimum":0,"maximum":300,"description":"For hours_percent."},"weekly_hours":{"type":"number","minimum":0,"maximum":80,"description":"For weekly_hours."},"name":{"type":"string","description":"For stop_recurring: the recurring item's name. Otherwise a label."},"account":{"type":"string","description":"Account name for purchases, money in, bills and income. Default: the main checking account."}},"required":["kind"],"additionalProperties":false}}},"required":["changes"],"additionalProperties":false}"""),

        new("list_accounts",
            "Every account with its id, name, type, whether it's archived, and current balance. Use ids from here for write proposals.",
            """{"type":"object","properties":{"include_archived":{"type":"boolean","description":"Default false."}},"additionalProperties":false}"""),

        new("list_categories",
            "Category tree with ids and names (groups and subcategories). Use ids or names for write proposals.",
            """{"type":"object","properties":{"include_archived":{"type":"boolean","description":"Default false."}},"additionalProperties":false}"""),

        new("list_jobs",
            "Hourly jobs with ids and names for paycheck and shift proposals.",
            """{"type":"object","properties":{"include_archived":{"type":"boolean","description":"Default false."}},"additionalProperties":false}"""),
    ];

    /// <summary>
    /// Runs a tool by name. Returns its JSON result, or a JSON object with an "error" the assistant
    /// can read and correct (an unknown tool, a bad date, an unknown account).
    /// </summary>
    public async Task<string> RunAsync(string name, JsonElement input, CancellationToken ct = default)
    {
        try
        {
            JsonNode result = name switch
            {
                "get_overview" => await OverviewAsync(ct),
                "get_budget" => await BudgetAsync(input, ct),
                "get_spending_history" => await SpendingHistoryAsync(input, ct),
                "search_transactions" => await TransactionsAsync(input, ct),
                "get_upcoming" => await UpcomingAsync(input, ct),
                "get_recurring" => await RecurringAsync(ct),
                "get_paycheck_estimates" => await PaychecksAsync(input, ct),
                "get_goals" => await GoalsAsync(ct),
                "can_i_afford" => await AffordAsync(input, ct),
                "what_if" => await WhatIfAsync(input, ct),
                "list_accounts" => await ListAccountsAsync(input, ct),
                "list_categories" => await ListCategoriesAsync(input, ct),
                "list_jobs" => await ListJobsAsync(input, ct),
                _ => throw new ToolInputException($"There is no tool called {name}."),
            };
            return result.ToJsonString();
        }
        catch (Exception ex) when (ex is ToolInputException or LedgerValidationException)
        {
            return new JsonObject { ["error"] = ex.Message }.ToJsonString();
        }
    }

    // ---- Tools ------------------------------------------------------------------------------

    private async Task<JsonNode> OverviewAsync(CancellationToken ct)
    {
        var today = Today;
        var balances = await accounts.GetAccountBalancesAsync(ct: ct);
        var worth = await accounts.GetNetWorthAsync(ct: ct);
        var forecast = await forecasts.GetForecastAsync(today, 14, null, ct);
        var month = await dashboard.GetMonthSummaryAsync(today, ct);
        var funds = (await goals.GetFundsAsync(ct)).Sum(f => f.CurrentCents);
        var (lowest, lowestDate) = forecast.Lowest;
        return new JsonObject
        {
            ["today"] = Iso(today),
            ["accounts"] = Array(balances.Select(b => new JsonObject
            {
                ["name"] = b.Account.Name,
                ["type"] = b.Account.AccountType.ToString(),
                ["balance"] = Dollars(b.BalanceCents),
                ["note"] = b.Account.IsLiability ? "credit card: balance is the amount owed" : null,
            })),
            ["available"] = Dollars(forecast.StartingBalanceCents),
            ["set_aside_in_sinking_funds"] = Dollars(funds),
            ["free_to_spend"] = Dollars(forecast.StartingBalanceCents - funds),
            ["credit_card_debt"] = Dollars(balances.Where(b => b.Account.IsLiability).Sum(b => b.BalanceCents)),
            ["net_worth"] = Dollars(worth.AssetsCents - worth.LiabilitiesCents),
            ["this_month_so_far"] = new JsonObject
            {
                ["from"] = Iso(month.From),
                ["through"] = Iso(month.Through),
                ["income"] = Dollars(month.IncomeCents),
                ["spending"] = Dollars(month.ExpenseCents),
                ["moved_to_savings"] = Dollars(month.SavedCents),
                ["remaining"] = Dollars(month.IncomeCents - month.ExpenseCents - month.SavedCents),
            },
            ["next_14_days"] = new JsonObject
            {
                ["expected_in"] = Dollars(forecast.ExpectedInCents),
                ["expected_out"] = Dollars(forecast.ExpectedOutCents),
                ["lowest_balance"] = Dollars(lowest),
                ["lowest_on"] = Iso(lowestDate),
                ["balance_after"] = Dollars(forecast.EndingBalanceCents),
            },
        };
    }

    private async Task<JsonNode> BudgetAsync(JsonElement input, CancellationToken ct)
    {
        var (year, month) = Month(input, "month") ?? (Today.Year, Today.Month);
        var plan = await budgets.GetMonthAsync(year, month, ct);
        return new JsonObject
        {
            ["month"] = $"{year:0000}-{month:00}",
            ["has_plan"] = plan.HasPlan,
            ["planned_total"] = Dollars(plan.PlannedCents),
            ["spent_total"] = Dollars(plan.ActualCents),
            ["remaining_total"] = Dollars(plan.RemainingCents),
            ["groups"] = Array(plan.Lines.Where(l => l.PlannedCents != 0 || l.ActualCents != 0).Select(l => new JsonObject
            {
                ["group"] = l.Name,
                ["planned"] = Dollars(l.PlannedCents),
                ["spent"] = Dollars(l.ActualCents),
                ["remaining"] = Dollars(l.RemainingCents),
                ["status"] = l.Status.ToString(),
            })),
        };
    }

    private async Task<JsonNode> SpendingHistoryAsync(JsonElement input, CancellationToken ct)
    {
        var months = Math.Clamp(Int(input, "months") ?? 4, 1, 12);
        var today = Today;
        var result = new JsonArray();
        var byGroup = new Dictionary<string, List<long>>();
        for (var i = months - 1; i >= 0; i--)
        {
            var first = new DateOnly(today.Year, today.Month, 1).AddMonths(-i);
            var partial = i == 0;
            var totals = await transactions.GetTotalsAsync(new TransactionQuery(From: first, To: partial ? today : first.AddMonths(1).AddDays(-1)), ct);
            var plan = await budgets.GetMonthAsync(first.Year, first.Month, ct);
            var groups = plan.Lines.Where(l => l.ActualCents != 0).OrderByDescending(l => l.ActualCents).ToList();
            if (!partial)
            {
                foreach (var g in plan.Lines)
                {
                    if (!byGroup.TryGetValue(g.Name, out var list)) byGroup[g.Name] = list = [];
                    list.Add(g.ActualCents);
                }
            }
            result.Add(new JsonObject
            {
                ["month"] = $"{first.Year:0000}-{first.Month:00}",
                ["partial_month_through"] = partial ? Iso(today) : null,
                ["income"] = Dollars(totals.IncomeCents),
                ["spending"] = Dollars(totals.ExpenseCents),
                ["refunds_included_in_spending"] = Dollars(totals.RefundCents),
                ["spending_by_group"] = Array(groups.Select(g => new JsonObject { ["group"] = g.Name, ["spent"] = Dollars(g.ActualCents) })),
            });
        }
        var complete = months - 1;
        return new JsonObject
        {
            ["months"] = result,
            ["complete_months_averaged"] = complete,
            ["average_per_complete_month_by_group"] = complete == 0
                ? null
                : Array(byGroup.Select(kv => (kv.Key, Avg: kv.Value.Sum() / (double)complete))
                    .Where(g => g.Avg != 0).OrderByDescending(g => g.Avg)
                    .Select(g => new JsonObject { ["group"] = g.Key, ["average"] = Dollars((long)Math.Round(g.Avg)) })),
        };
    }

    private async Task<JsonNode> TransactionsAsync(JsonElement input, CancellationToken ct)
    {
        var limit = Math.Clamp(Int(input, "limit") ?? 50, 1, MaxTransactions);
        int? categoryId = null;
        if (Str(input, "category") is { } categoryName)
        {
            var all = (await categories.GetTreeAsync(includeArchived: true, ct: ct)).SelectMany(n => n.Children.Prepend(n.Category)).ToList();
            categoryId = all.FirstOrDefault(c => string.Equals(c.Name, categoryName.Trim(), StringComparison.OrdinalIgnoreCase))?.Id
                ?? throw new ToolInputException($"There's no category called \"{categoryName}\". Known: {string.Join(", ", all.Where(c => !c.IsArchived).Select(c => c.Name))}.");
        }
        TransactionType? type = Str(input, "kind") switch
        {
            null => null,
            "spending" => TransactionType.Expense,
            "income" => TransactionType.Income,
            "transfer" => TransactionType.Transfer,
            "refund" => TransactionType.Refund,
            var other => throw new ToolInputException($"Unknown kind \"{other}\"."),
        };
        var query = new TransactionQuery(Str(input, "search"), Date(input, "from"), Date(input, "to"), null, categoryId, type);
        var list = await transactions.QueryAsync(query, ct);
        var totals = await transactions.GetTotalsAsync(query, ct);
        return new JsonObject
        {
            ["matched"] = totals.Count,
            ["shown"] = Math.Min(limit, list.Count),
            ["income_total"] = Dollars(totals.IncomeCents),
            ["spending_total"] = Dollars(totals.ExpenseCents),
            ["transactions"] = Array(list.Take(limit).Select(t => new JsonObject
            {
                ["date"] = Iso(t.Date),
                ["kind"] = Kind(t.Type),
                ["amount"] = Dollars(t.AmountCents),
                ["payee"] = t.Payee,
                ["category"] = t.CategoryFullName,
                ["account"] = t.TransferAccountName is { } to ? $"{t.AccountName} → {to}" : t.AccountName,
                ["note"] = t.Memo,
                ["pending"] = t.Status == TransactionStatus.Pending ? true : null,
            })),
        };
    }

    private async Task<JsonNode> UpcomingAsync(JsonElement input, CancellationToken ct)
    {
        var days = Math.Clamp(Int(input, "days") ?? 30, 1, MaxForecastDays);
        var forecast = await forecasts.GetForecastAsync(Today, days, null, ct);
        var (lowest, lowestDate) = forecast.Lowest;
        return new JsonObject
        {
            ["from"] = Iso(forecast.Today),
            ["through"] = Iso(forecast.Through),
            ["starting_balance_today"] = Dollars(forecast.StartingBalanceCents),
            ["accounts_covered"] = Array(forecast.Accounts.Select(a => (JsonNode)JsonValue.Create(a.Name)!)),
            ["lowest_balance"] = Dollars(lowest),
            ["lowest_on"] = Iso(lowestDate),
            ["ending_balance"] = Dollars(forecast.EndingBalanceCents),
            ["events"] = Array(forecast.Events.Select(e => new JsonObject
            {
                ["date"] = Iso(e.Date),
                ["name"] = e.Name,
                ["amount"] = Dollars(e.SignedCents),
                ["kind"] = e.Kind.ToString(),
                ["estimated"] = e.IsEstimated ? true : null,
                ["overdue_from"] = e.IsOverdue ? Iso(e.ScheduledDate!.Value) : null,
                ["balance_after"] = Dollars(e.BalanceAfterCents),
            })),
        };
    }

    private async Task<JsonNode> RecurringAsync(CancellationToken ct)
    {
        var today = Today;
        var rules = await recurring.GetRulesAsync(ct);
        var occurrences = await recurring.GetOccurrencesAsync(today.AddDays(-400), today.AddDays(400), ct);
        return new JsonObject
        {
            ["items"] = Array(rules.Select(r => new JsonObject
            {
                ["name"] = r.Name,
                ["kind"] = Kind(r.TransactionType),
                ["amount"] = Dollars(r.AmountCents),
                ["estimated_amount"] = r.IsAmountEstimated ? true : null,
                ["frequency"] = r.Frequency.ToString().ToLowerInvariant(),
                ["per_month"] = Dollars(RecurrenceSchedule.MonthlyEquivalent(r.AmountCents, r.Frequency)),
                ["next_date"] = RecurringService.NextDate(r, occurrences, today) is { } next ? Iso(next) : null,
                ["paused"] = r.IsActive ? null : true,
                ["ends"] = r.EndDate is { } end ? Iso(end) : null,
            })),
        };
    }

    private async Task<JsonNode> PaychecksAsync(JsonElement input, CancellationToken ct)
    {
        var days = Math.Clamp(Int(input, "days") ?? 45, 1, 120);
        var paychecks = await income.GetPaychecksAsync(Today.AddDays(-14), Today.AddDays(days), ct);
        return new JsonObject
        {
            ["note"] = "Estimates from scheduled shifts; they're forecasts until the real paycheck is recorded.",
            ["paychecks"] = Array(paychecks.Select(p => new JsonObject
            {
                ["job"] = p.JobName,
                ["pay_date"] = Iso(p.Period.PayDate),
                ["hours"] = Math.Round(p.TotalMinutes / 60m, 2),
                ["gross"] = Dollars(p.GrossCents),
                ["take_home_estimate"] = Dollars(p.NetCents),
                ["recorded_actual"] = p.Recorded is { } r ? Dollars(r.ActualCents) : null,
                ["payday_confirmed"] = p.IsPaydayConfirmed,
            })),
        };
    }

    private async Task<JsonNode> GoalsAsync(CancellationToken ct)
    {
        var all = await goals.GetGoalsAsync(ct: ct);
        return new JsonObject
        {
            ["goals"] = Array(all.Select(g => new JsonObject
            {
                ["name"] = g.Goal.Name,
                ["sinking_fund"] = g.Goal.IsSinkingFund ? true : null,
                ["target"] = Dollars(g.TargetCents),
                ["saved"] = Dollars(g.CurrentCents),
                ["percent"] = g.Percent,
                ["target_date"] = g.Goal.TargetDate is { } d ? Iso(d) : null,
                ["suggested_monthly"] = g.SuggestedMonthlyCents is { } s ? Dollars(s) : null,
                ["monthly_set_aside"] = g.Goal.MonthlySetAsideCents is { } m ? Dollars(m) : null,
                ["pays_for_category"] = g.Goal.Category?.Name,
                ["follows_account"] = g.Goal.Tracking == GoalTracking.AccountBalance ? g.Goal.Account?.Name : null,
            })),
        };
    }

    private async Task<JsonNode> AffordAsync(JsonElement input, CancellationToken ct)
    {
        var amount = Cents(input, "amount") ?? throw new ToolInputException("amount is required.");
        var date = Date(input, "date") ?? Today;
        var account = await AccountIdAsync(Str(input, "account"), ct);
        var cushion = Cents(input, "cushion") ?? 0;
        var answer = await scenarios.CanIAffordAsync(Today, amount, date, account, cushion, ct);
        return new JsonObject
        {
            ["affordable"] = answer.Affordable,
            ["answer"] = answer.Answer,
            ["without_it"] = Outcome(answer.Without),
            ["with_it"] = Outcome(answer.With),
            ["looked_ahead_through"] = Iso(date.AddDays(60)),
        };
    }

    private async Task<JsonNode> ListAccountsAsync(JsonElement input, CancellationToken ct)
    {
        var includeArchived = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("include_archived", out var a) && a.ValueKind == JsonValueKind.True;
        var balances = await accounts.GetAccountBalancesAsync(includeArchived, ct: ct);
        return new JsonObject
        {
            ["accounts"] = Array(balances.Select(b => new JsonObject
            {
                ["id"] = b.Account.Id,
                ["name"] = b.Account.Name,
                ["type"] = b.Account.AccountType.ToString(),
                ["archived"] = b.Account.IsArchived ? true : null,
                ["balance"] = Dollars(b.BalanceCents),
            })),
        };
    }

    private async Task<JsonNode> ListCategoriesAsync(JsonElement input, CancellationToken ct)
    {
        var includeArchived = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("include_archived", out var a) && a.ValueKind == JsonValueKind.True;
        var tree = await categories.GetTreeAsync(includeArchived, ct: ct);
        return new JsonObject
        {
            ["categories"] = Array(tree.Select(n => new JsonObject
            {
                ["id"] = n.Category.Id,
                ["name"] = n.Category.Name,
                ["type"] = n.Category.CategoryType switch
                {
                    CategoryType.Expense => "spending",
                    CategoryType.Income => "income",
                    _ => "savings",
                },
                ["archived"] = n.Category.IsArchived ? true : null,
                ["children"] = Array(n.Children.Select(c => new JsonObject
                {
                    ["id"] = c.Id,
                    ["name"] = c.Name,
                    ["archived"] = c.IsArchived ? true : null,
                })),
            })),
        };
    }

    private async Task<JsonNode> ListJobsAsync(JsonElement input, CancellationToken ct)
    {
        var includeArchived = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("include_archived", out var a) && a.ValueKind == JsonValueKind.True;
        var list = await jobs.GetJobsAsync(includeArchived, ct);
        return new JsonObject
        {
            ["jobs"] = Array(list.Select(j => new JsonObject
            {
                ["id"] = j.Id,
                ["name"] = j.Name,
                ["archived"] = j.IsArchived ? true : null,
            })),
        };
    }

    private async Task<JsonNode> WhatIfAsync(JsonElement input, CancellationToken ct)
    {
        var days = Math.Clamp(Int(input, "days") ?? 90, 7, MaxForecastDays);
        if (!input.TryGetProperty("changes", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
        {
            throw new ToolInputException("changes must be a non-empty list.");
        }
        var rules = await recurring.GetRulesAsync(ct);
        var changes = new List<ScenarioChange>();
        foreach (var c in list.EnumerateArray())
        {
            var from = Date(c, "date") ?? Today;
            var name = Str(c, "name");
            changes.Add(Str(c, "kind") switch
            {
                "purchase" => new(ScenarioChangeKind.Purchase, from, Need(Cents(c, "amount")), await AccountIdAsync(Str(c, "account"), ct), Name: name),
                "extra_income" => new(ScenarioChangeKind.ExtraIncome, from, Need(Cents(c, "amount")), await AccountIdAsync(Str(c, "account"), ct), Name: name),
                "new_bill" => new(ScenarioChangeKind.AddBill, from, Need(Cents(c, "amount")), await AccountIdAsync(Str(c, "account"), ct), Frequency: Frequency(c), Name: name ?? "New bill"),
                "new_income" => new(ScenarioChangeKind.AddIncome, from, Need(Cents(c, "amount")), await AccountIdAsync(Str(c, "account"), ct), Frequency: Frequency(c), Name: name ?? "New income"),
                "stop_recurring" => new(ScenarioChangeKind.StopRecurring, from, RecurringId: (rules.FirstOrDefault(r => string.Equals(r.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase))
                    ?? throw new ToolInputException($"There's no recurring item called \"{name}\". Known: {string.Join(", ", rules.Select(r => r.Name))}.")).Id, Name: name),
                "hours_percent" => new(ScenarioChangeKind.HoursPercent, from, Percent: Int(c, "percent") ?? throw new ToolInputException("percent is required for hours_percent.")),
                "weekly_hours" => new(ScenarioChangeKind.WeeklyHours, from, WeeklyMinutes: (int)Math.Round((Number(c, "weekly_hours") ?? throw new ToolInputException("weekly_hours is required.")) * 60)),
                "pay_rate" => new(ScenarioChangeKind.PayRate, from, Need(Cents(c, "amount"))),
                var other => throw new ToolInputException($"Unknown change kind \"{other}\"."),
            });
        }
        var comparison = await scenarios.CompareAsync(Today, days, new Scenario("Assistant what-if", changes), 0, null, ct);
        return new JsonObject
        {
            ["through"] = Iso(Today.AddDays(days)),
            ["as_things_are"] = Outcome(comparison.Before),
            ["with_changes"] = Outcome(comparison.After),
            ["goals_affected"] = Array(comparison.Goals.Select(g => new JsonObject
            {
                ["goal"] = g.GoalName,
                ["target_date"] = Iso(g.TargetDate),
                ["target"] = Dollars(g.TargetCents),
                ["account_then_as_things_are"] = Dollars(g.BaselineCents),
                ["account_then_with_changes"] = Dollars(g.ScenarioCents),
            })),
        };
    }

    // ---- Helpers ----------------------------------------------------------------------------

    private static JsonObject Outcome(ForecastOutcome o) => new()
    {
        ["lowest_balance"] = Dollars(o.LowestCents),
        ["lowest_on"] = Iso(o.LowestDate),
        ["ending_balance"] = Dollars(o.EndingCents),
        ["first_below_zero"] = o.BelowZeroOn is { } z ? Iso(z) : null,
        ["first_below_cushion"] = o.BelowCushionOn is { } c ? Iso(c) : null,
    };

    /// <summary>The account named <paramref name="name"/>, or the main checking account (else the first open non-card one).</summary>
    private async Task<int> AccountIdAsync(string? name, CancellationToken ct)
    {
        var open = (await accounts.GetAccountBalancesAsync(ct: ct)).Select(b => b.Account).ToList();
        if (name is not null)
        {
            return open.FirstOrDefault(a => string.Equals(a.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))?.Id
                ?? throw new ToolInputException($"There's no open account called \"{name}\". Accounts: {string.Join(", ", open.Select(a => a.Name))}.");
        }
        return (open.FirstOrDefault(a => a.AccountType == AccountType.Checking) ?? open.FirstOrDefault(a => !a.IsLiability)
                ?? throw new ToolInputException("There are no open accounts.")).Id;
    }

    private static JsonArray Array(IEnumerable<JsonNode> items) => new([.. items]);

    private static JsonNode Dollars(long cents) => JsonValue.Create(cents / 100m);

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Kind(TransactionType t) => t switch
    {
        TransactionType.Income => "income",
        TransactionType.Expense => "spending",
        TransactionType.Transfer => "transfer",
        TransactionType.Refund => "refund",
        TransactionType.OutsideIn => "money in from someone else's account",
        TransactionType.OutsideOut => "money out to someone else's account",
        _ => t.ToString(),
    };

    private static long Need(long? cents) => cents is > 0 ? cents.Value : throw new ToolInputException("amount must be greater than zero.");

    private static RecurrenceFrequency Frequency(JsonElement e) => Str(e, "frequency") switch
    {
        null or "monthly" => RecurrenceFrequency.Monthly,
        "weekly" => RecurrenceFrequency.Weekly,
        "biweekly" => RecurrenceFrequency.Biweekly,
        "quarterly" => RecurrenceFrequency.Quarterly,
        "yearly" => RecurrenceFrequency.Yearly,
        var other => throw new ToolInputException($"Unknown frequency \"{other}\"."),
    };

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    private static decimal? Number(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;

    private static int? Int(JsonElement e, string name) => Number(e, name) is { } d ? (int)Math.Round(d) : null;

    private static long? Cents(JsonElement e, string name) => Number(e, name) is { } d ? (long)Math.Round(d * 100, MidpointRounding.AwayFromZero) : null;

    private static DateOnly? Date(JsonElement e, string name) => Str(e, name) is not { } s
        ? null
        : DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : throw new ToolInputException($"{name} must be a date like 2026-10-15.");

    private static (int Year, int Month)? Month(JsonElement e, string name) => Str(e, name) is not { } s
        ? null
        : DateOnly.TryParseExact(s + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? (d.Year, d.Month)
            : throw new ToolInputException($"{name} must look like 2026-10.");

    private sealed class ToolInputException(string message) : Exception(message);
}

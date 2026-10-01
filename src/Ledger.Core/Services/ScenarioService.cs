using System.Text.Json;
using Ledger.Core.Forecasting;
using Ledger.Core.Models;

namespace Ledger.Core.Services;

/// <summary>What a forecast comes to: its lowest point, where it ends, and when it first drops below zero or a cushion.</summary>
public sealed record ForecastOutcome(long LowestCents, DateOnly LowestDate, long EndingCents, DateOnly? BelowZeroOn, DateOnly? BelowCushionOn)
{
    public static ForecastOutcome Of(Forecast forecast, long cushionCents)
    {
        var (lowest, lowestDate) = forecast.Lowest;
        DateOnly? FirstBelow(long line) => forecast.StartingBalanceCents < line
            ? forecast.Today
            : forecast.Events.Where(e => e.BalanceAfterCents < line).Select(e => (DateOnly?)e.Date).FirstOrDefault();
        return new ForecastOutcome(lowest, lowestDate, forecast.EndingBalanceCents, FirstBelow(0), cushionCents > 0 ? FirstBelow(cushionCents) : null);
    }
}

/// <summary>An account-backed goal's projected balance on its target date, without and with the what-if.</summary>
public sealed record GoalImpact(string GoalName, DateOnly TargetDate, long TargetCents, long BaselineCents, long ScenarioCents);

/// <summary>The forecast as things are, beside the forecast with a scenario's assumptions.</summary>
public sealed record ScenarioComparison(Forecast Baseline, Forecast WithScenario, ForecastOutcome Before, ForecastOutcome After, IReadOnlyList<GoalImpact> Goals);

/// <summary>A plain answer to "Can I afford this?", with the outcomes it's based on.</summary>
public sealed record AffordAnswer(bool Affordable, string Answer, ForecastOutcome Without, ForecastOutcome With);

/// <summary>
/// What-if planning on top of the forecast: compare a scenario with things as they are, answer "Can
/// I afford this?", and keep named scenarios to reopen. Scenarios never change transactions, bills,
/// goals or anything else; only the saved list of scenarios itself is stored.
/// </summary>
public sealed class ScenarioService(ForecastService forecasts, GoalService goals, SettingsService settings)
{
    public const int MaxDays = 366;

    public async Task<ScenarioComparison> CompareAsync(DateOnly today, int days, Scenario scenario, long cushionCents = 0, int? accountId = null, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, MaxDays);
        var baseline = await forecasts.GetForecastAsync(today, days, accountId, null, ct);
        var withScenario = await forecasts.GetForecastAsync(today, days, accountId, scenario, ct);

        // Goals that follow an account: where that account would be on the target date.
        var impacts = new List<GoalImpact>();
        foreach (var g in (await goals.GetGoalsAsync(ct: ct)).Where(g => g.Goal.Tracking == GoalTracking.AccountBalance && g.Goal.AccountId is not null
                     && g.Goal.TargetDate is { } d && d > today && d <= today.AddDays(days)))
        {
            var target = g.Goal.TargetDate!.Value;
            var span = target.DayNumber - today.DayNumber;
            var before = await forecasts.GetForecastAsync(today, span, g.Goal.AccountId, null, ct);
            var after = await forecasts.GetForecastAsync(today, span, g.Goal.AccountId, scenario, ct);
            if (before.EndingBalanceCents != after.EndingBalanceCents)
            {
                impacts.Add(new GoalImpact(g.Goal.Name, target, g.TargetCents, before.EndingBalanceCents, after.EndingBalanceCents));
            }
        }
        return new ScenarioComparison(baseline, withScenario, ForecastOutcome.Of(baseline, cushionCents), ForecastOutcome.Of(withScenario, cushionCents), impacts);
    }

    /// <summary>
    /// Would a purchase of <paramref name="amountCents"/> on <paramref name="date"/> from <paramref name="accountId"/>
    /// keep the balance at or above zero (and the cushion, if one is set) for at least 60 days after it?
    /// </summary>
    public async Task<AffordAnswer> CanIAffordAsync(DateOnly today, long amountCents, DateOnly date, int accountId, long cushionCents = 0, CancellationToken ct = default)
    {
        if (amountCents <= 0) throw new LedgerValidationException("Enter an amount greater than zero.");
        if (date < today) throw new LedgerValidationException("Choose today or a later date.");
        var days = Math.Min(MaxDays, date.DayNumber - today.DayNumber + 60);
        var scenario = new Scenario("Can I afford this?", [new ScenarioChange(ScenarioChangeKind.Purchase, date, amountCents, accountId, Name: "This purchase")]);
        var comparison = await CompareAsync(today, days, scenario, cushionCents, null, ct);
        var (before, after) = (comparison.Before, comparison.After);
        string Lowest() => $"your lowest point would be {Money.Format(after.LowestCents)} on {after.LowestDate:MMM d}";
        var (ok, answer) = after switch
        {
            // Already short without it: say so, rather than blaming the purchase.
            { BelowZeroOn: { } } when before.BelowZeroOn is { } already =>
                (false, $"Not right now: even without it, your balance is forecast to go below $0 on {already:MMM d}. With it, {Lowest()}."),
            { BelowZeroOn: { } below } => (false, $"Not without going below $0: {Lowest()}, first dropping below zero on {below:MMM d}."),
            { BelowCushionOn: { } below } => (false, $"Only by dipping into your {Money.Format(cushionCents)} cushion: {Lowest()}, below the cushion from {below:MMM d}."),
            _ => (true, $"Yes. With it, {Lowest()}."),
        };
        return new AffordAnswer(ok, answer, before, after);
    }

    // ---- Saved scenarios (only the assumptions are stored, as preferences) --------------------------

    public async Task<IReadOnlyList<Scenario>> GetSavedAsync(CancellationToken ct = default)
    {
        var json = await settings.GetAsync(SettingKeys.Scenarios, ct);
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<Scenario>>(json) ?? [];
        }
        catch (JsonException)
        {
            return []; // a damaged list is ignored rather than breaking the page
        }
    }

    /// <summary>Saves (or replaces, by name) a scenario's assumptions.</summary>
    public async Task SaveAsync(Scenario scenario, CancellationToken ct = default)
    {
        var name = scenario.Name.Trim();
        if (name.Length == 0) throw new LedgerValidationException("Give the scenario a name to save it.");
        if (name.Length > 100) throw new LedgerValidationException("Names can be up to 100 characters.");
        var saved = (await GetSavedAsync(ct)).Where(s => !string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        saved.Add(scenario with { Name = name });
        await settings.SetAsync(SettingKeys.Scenarios, JsonSerializer.Serialize(saved.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList()), ct);
    }

    public async Task DeleteAsync(string name, CancellationToken ct = default)
    {
        var saved = (await GetSavedAsync(ct)).Where(s => !string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        await settings.SetAsync(SettingKeys.Scenarios, JsonSerializer.Serialize(saved), ct);
    }
}

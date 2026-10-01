using Ledger.Core.Models;

namespace Ledger.Core.Forecasting;

public enum ScenarioChangeKind
{
    /// <summary>A one-off purchase on <see cref="ScenarioChange.From"/> from <see cref="ScenarioChange.AccountId"/>.</summary>
    Purchase = 0,

    /// <summary>One-off money in (a gift, a sale) on <see cref="ScenarioChange.From"/>.</summary>
    ExtraIncome = 1,

    /// <summary>Scheduled shifts from <see cref="ScenarioChange.From"/> at <see cref="ScenarioChange.Percent"/>% of their length.</summary>
    HoursPercent = 2,

    /// <summary>From <see cref="ScenarioChange.From"/>, <see cref="ScenarioChange.WeeklyMinutes"/> paid minutes every week instead of the scheduled shifts.</summary>
    WeeklyHours = 3,

    /// <summary>A new hourly rate (<see cref="ScenarioChange.AmountCents"/>) from <see cref="ScenarioChange.From"/>.</summary>
    PayRate = 4,

    /// <summary>A new recurring bill (money out) starting on <see cref="ScenarioChange.From"/>.</summary>
    AddBill = 5,

    /// <summary>New recurring income starting on <see cref="ScenarioChange.From"/>.</summary>
    AddIncome = 6,

    /// <summary>An existing recurring item (<see cref="ScenarioChange.RecurringId"/>) stops from <see cref="ScenarioChange.From"/>.</summary>
    StopRecurring = 7,
}

/// <summary>
/// One assumption in a what-if. Only the fields its <see cref="Kind"/> uses matter. A null
/// <see cref="JobId"/> on an hours or pay change means every job.
/// </summary>
public sealed record ScenarioChange(
    ScenarioChangeKind Kind,
    DateOnly From,
    long AmountCents = 0,
    int? AccountId = null,
    int? JobId = null,
    int Percent = 100,
    int WeeklyMinutes = 0,
    RecurrenceFrequency Frequency = RecurrenceFrequency.Monthly,
    int? RecurringId = null,
    string? Name = null);

/// <summary>
/// A named set of what-if assumptions applied on top of the forecast. Scenarios never become real
/// data: they only change what the forecast predicts.
/// </summary>
public sealed record Scenario(string Name, IReadOnlyList<ScenarioChange> Changes)
{
    public static Scenario Empty { get; } = new("", []);
}

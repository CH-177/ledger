using Ledger.Core.Models;
using Ledger.Core.Payroll;
using Ledger.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Core.Tests;

public class IncomeForecastTests
{
    private static readonly DateOnly Today = new(2026, 9, 27); // Sunday

    private sealed class Fixture : IAsyncDisposable
    {
        public required TestDatabase Db { get; init; }
        public required IncomeService Income { get; init; }
        public required IncomeForecastService Forecast { get; init; }
        public required Job Market { get; init; }
        public ValueTask DisposeAsync() => Db.DisposeAsync();

        public Task AddAsync(int month, int day, int minutes, ShiftStatus status = ShiftStatus.Scheduled, int? breakOverride = null) =>
            Income.AddShiftAsync(new ShiftInput(Market.Id, new DateOnly(2026, month, day), minutes, null, null, breakOverride, status, null));

    }

    private static JobInput MarketInput(long rateCents = 1350) => new(
        Name: "Riverside Market",
        HourlyRateCents: rateCents,
        RateEffectiveDate: new DateOnly(2026, 9, 1),
        PayFrequency: PayFrequency.Weekly,
        PayPeriodAnchor: new DateOnly(2026, 9, 20),   // a Sunday
        PaydayOffsetDays: 6,                          // Saturday end → Friday
        IsPaydayConfirmed: false,
        WorkweekStart: DayOfWeek.Sunday,
        IsOvertimeEligible: true,
        DepositAccountId: null,
        IncomeCategoryId: null,
        FilingStatus: FilingStatus.Single,
        W4MultipleJobs: false,
        W4DependentsCreditCents: 0,
        W4OtherIncomeCents: 0,
        W4DeductionsCents: 0,
        W4ExtraWithholdingCents: 0,
        WorkLocationName: "Riverton City",
        WorkPsdCode: "990201",
        WorkNonResidentEitBasisPoints: 50,
        WorkLstAnnualCents: 5200,
        IsLstExempt: false);

    /// <summary>A Pennsylvania home at 1.5% (Maple Boro, the default before 3.0.0).</summary>
    internal static readonly HomeTaxSettings MapleBoro = new("Maple Boro", "990101", 150);

    private static async Task<Fixture> CreateAsync()
    {
        var db = await TestDatabase.CreateAsync(Today);
        var income = new IncomeService(db.Factory, new SettingsService(db.Factory));
        await income.SetHomeTaxSettingsAsync(MapleBoro);
        var job = await income.CreateJobAsync(MarketInput());
        return new Fixture { Db = db, Income = income, Forecast = new IncomeForecastService(db.Factory, income), Market = job };
    }

    /// <summary>The schedule from the user's message, plus 4 paid training hours the week before.</summary>
    private static async Task AddYourScheduleAsync(Fixture f)
    {
        await f.AddAsync(9, 24, 240, ShiftStatus.Worked);   // training
        foreach (var (m, d, min) in new[] { (9, 29, 240), (9, 30, 240), (10, 2, 360), (10, 7, 270), (10, 9, 420), (10, 10, 480) })
        {
            await f.AddAsync(m, d, min);
        }
    }

    // ---- Home settings -------------------------------------------------------------------------

    [Fact]
    public async Task Home_starts_unset_and_can_be_changed()
    {
        await using var db = await TestDatabase.CreateAsync(Today);
        var fresh = new IncomeService(db.Factory, new SettingsService(db.Factory));
        var unset = await fresh.GetHomeTaxSettingsAsync();
        Assert.Equal(IncomeService.DefaultHome, unset);
        Assert.False(unset.IsSet);
        Assert.True(unset.InPennsylvania);

        await using var f = await CreateAsync();
        Assert.Equal(MapleBoro, await f.Income.GetHomeTaxSettingsAsync());

        await f.Income.SetHomeTaxSettingsAsync(new HomeTaxSettings("Elsewhere", "999999", 100));
        Assert.Equal(100, (await f.Income.GetHomeTaxSettingsAsync()).ResidentEitBasisPoints);
        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Income.SetHomeTaxSettingsAsync(new HomeTaxSettings("x", null, 1001)));
    }

    // ---- End-to-end: the sample schedule --------------------------------------------------

    [Fact]
    public async Task Your_schedule_becomes_three_weekly_paychecks()
    {
        await using var f = await CreateAsync();
        await AddYourScheduleAsync(f);

        var paychecks = await f.Forecast.GetPaychecksAsync(new DateOnly(2026, 9, 1), new DateOnly(2026, 12, 31));

        Assert.Equal(
        [
            (new DateOnly(2026, 10, 2), 240, 5400L),    // training, week of Sep 20
            (new DateOnly(2026, 10, 9), 810, 18225L),   // 4 + 4 + 5.5 paid hours (6h shift minus 30 min unpaid break)
            (new DateOnly(2026, 10, 16), 1110, 24975L), // 4.5 + 6.5 + 7.5 paid hours
        ], paychecks.Select(p => (p.Period.PayDate, p.TotalMinutes, p.GrossCents)));

        Assert.Equal(15885, paychecks[1].NetCents);   // $182.25 − $23.40 withheld
        Assert.All(paychecks, p => Assert.False(p.IsPaydayConfirmed));
        Assert.False(paychecks[0].HasUnconfirmedShifts);
        Assert.True(paychecks[1].HasUnconfirmedShifts);
        Assert.All(paychecks, p => Assert.Equal(100, p.Breakdown.Amount(PaycheckLineKind.LocalServicesTax)));
    }

    [Fact]
    public async Task Totals_by_payday_and_by_work_date()
    {
        await using var f = await CreateAsync();
        await AddYourScheduleAsync(f);

        var byPayday = await f.Forecast.GetWindowsAsync(Today, byWorkDate: false);
        var byWork = await f.Forecast.GetWindowsAsync(Today, byWorkDate: true);

        // This week (Sun Sep 27 – Sat Oct 3): only the training paycheck is paid, on Fri Oct 2;
        // but 13.5 paid hours ($182.25) are earned.
        Assert.Equal((5400L, 1), (byPayday[0].GrossCents, byPayday[0].PaycheckCount));
        Assert.Equal(18225L, byWork[0].GrossCents);

        // Next 2 weeks (Sep 27 – Oct 10): paydays Oct 2 and Oct 9; everything scheduled is earned.
        Assert.Equal(5400 + 18225, byPayday[1].GrossCents);
        Assert.Equal(18225 + 24975, byWork[1].GrossCents);

        // September: nothing is paid in September; $54 + $108 is earned in September.
        Assert.Equal(0, byPayday[2].GrossCents);
        Assert.Equal(5400 + 10800, byWork[2].GrossCents);
    }

    [Fact]
    public async Task By_work_date_net_is_the_paychecks_net_share()
    {
        await using var f = await CreateAsync();
        await AddYourScheduleAsync(f);
        var september = (await f.Forecast.GetWindowsAsync(Today, byWorkDate: true))[2];
        Assert.True(september.NetCents < september.GrossCents && september.NetCents > september.GrossCents * 8 / 10);
    }

    [Fact]
    public async Task Lst_stops_at_52_dollars_in_a_year()
    {
        await using var f = await CreateAsync();
        // 2027 has 53 Fridays (Jan 1 … Dec 31), so weekly pay gives 53 paydays: $1 each would be
        // $53 without the cap. The week of Dec 20, 2026 is paid on Fri Jan 1, 2027.
        var start = new DateOnly(2026, 12, 21);
        for (var i = 0; i < 60; i++)
        {
            await f.Income.AddShiftAsync(new ShiftInput(f.Market.Id, start.AddDays(7 * i), 60, null, null, null, ShiftStatus.Scheduled, null));
        }
        var paychecks = await f.Forecast.GetPaychecksAsync(new DateOnly(2027, 1, 1), new DateOnly(2028, 12, 31));
        var in2027 = paychecks.Where(p => p.Period.PayDate.Year == 2027).ToList();
        Assert.Equal(53, in2027.Count);
        Assert.Equal(5200, in2027.Sum(p => p.Breakdown.Amount(PaycheckLineKind.LocalServicesTax)));
        Assert.Equal(0, in2027[^1].Breakdown.Amount(PaycheckLineKind.LocalServicesTax));
        Assert.True(paychecks.First(p => p.Period.PayDate.Year == 2028).Breakdown.Amount(PaycheckLineKind.LocalServicesTax) > 0);
    }

    [Fact]
    public async Task Overtime_is_counted_per_workweek()
    {
        await using var f = await CreateAsync();
        for (var d = 4; d <= 8; d++) await f.AddAsync(10, d, 9 * 60, breakOverride: 0); // 45 paid hours in the week of Oct 4
        var p = Assert.Single(await f.Forecast.GetPaychecksAsync(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)));
        Assert.Equal((2400, 300), (p.RegularMinutes, p.OvertimeMinutes));
        Assert.Equal(40 * 1350 + 5 * 2025, p.GrossCents);
        Assert.Equal((5 * 2025L, 40 * 1350L), (p.OvertimePayCents, p.RegularPayCents));
    }

    [Fact]
    public async Task Overtime_after_a_mid_week_raise_is_paid_at_the_new_rate()
    {
        await using var f = await CreateAsync();
        // Raise to $14.50 from Thursday Oct 8; 9 h a day Sun–Thu = 45 h, the last 5 h (Thursday) are overtime.
        await f.Income.UpdateJobAsync(f.Market.Id, MarketInput(1450) with { RateEffectiveDate = new DateOnly(2026, 10, 8) });
        for (var d = 4; d <= 8; d++) await f.AddAsync(10, d, 9 * 60, breakOverride: 0);

        var p = Assert.Single(await f.Forecast.GetPaychecksAsync(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)));
        Assert.Equal(5 * 2175L, p.OvertimePayCents);                  // 5 h × $14.50 × 1.5, not the old rate
        Assert.Equal(36 * 1350L + 4 * 1450L, p.RegularPayCents);       // Sun–Wed at $13.50, 4 h Thursday at $14.50
        Assert.Equal(p.GrossCents, p.RegularPayCents + p.OvertimePayCents);
    }

    [Fact]
    public async Task A_raise_only_affects_shifts_from_its_date()
    {
        await using var f = await CreateAsync();
        await f.AddAsync(10, 5, 60);
        await f.AddAsync(10, 12, 60);
        await f.Income.UpdateJobAsync(f.Market.Id, MarketInput(1450) with { RateEffectiveDate = new DateOnly(2026, 10, 11) });

        var paychecks = await f.Forecast.GetPaychecksAsync(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));
        Assert.Equal([1350L, 1450L], paychecks.Select(p => p.GrossCents));
        Assert.Equal(2, (await f.Income.GetJobAsync(f.Market.Id)).Rates.Count);
    }

    [Fact]
    public async Task Archived_jobs_are_not_predicted()
    {
        await using var f = await CreateAsync();
        await AddYourScheduleAsync(f);
        await f.Income.SetJobArchivedAsync(f.Market.Id, true);
        Assert.Empty(await f.Forecast.GetPaychecksAsync(new DateOnly(2026, 9, 1), new DateOnly(2026, 12, 31)));
    }

    [Fact]
    public async Task Predictions_never_create_transactions()
    {
        await using var f = await CreateAsync();
        await AddYourScheduleAsync(f);
        await f.Forecast.GetPaychecksAsync(new DateOnly(2026, 9, 1), new DateOnly(2026, 12, 31));
        await f.Forecast.GetWindowsAsync(Today, true);

        await using var db = f.Db.Context();
        Assert.Equal(0, await db.Transactions.IgnoreQueryFilters().CountAsync());
    }

    // ---- Recording the real paycheck ----------------------------------------------------------

    [Fact]
    public async Task Recording_links_the_actual_paycheck_without_changing_the_estimate()
    {
        await using var f = await CreateAsync();
        await AddYourScheduleAsync(f);
        var account = await f.Db.AddAccountAsync("Checking", AccountType.Checking);
        var predicted = (await f.Forecast.GetPaychecksAsync(new DateOnly(2026, 10, 9), new DateOnly(2026, 10, 9))).Single();

        var tx = await new TransactionService(f.Db.Factory, f.Db.Time).CreateAsync(
            new TransactionInput(TransactionType.Income, 16312, new DateOnly(2026, 10, 9), account.Id, Payee: "Riverside Market"));
        await f.Forecast.RecordPaycheckAsync(f.Market.Id, predicted.Period, predicted.NetCents, tx.Id);

        var after = (await f.Forecast.GetPaychecksAsync(new DateOnly(2026, 10, 9), new DateOnly(2026, 10, 9))).Single();
        Assert.Equal(new RecordedPaycheck(tx.Id, 16312), after.Recorded);
        Assert.Equal(15885, after.NetCents);   // the estimate is unchanged
    }

    [Fact]
    public async Task Deleting_the_recorded_paycheck_makes_it_unrecorded_and_it_can_be_recorded_again()
    {
        await using var f = await CreateAsync();
        await AddYourScheduleAsync(f);
        var account = await f.Db.AddAccountAsync("Checking", AccountType.Checking);
        var transactions = new TransactionService(f.Db.Factory, f.Db.Time);
        var day = new DateOnly(2026, 10, 9);
        var predicted = (await f.Forecast.GetPaychecksAsync(day, day)).Single();

        var first = await transactions.CreateAsync(new TransactionInput(TransactionType.Income, 16312, day, account.Id));
        await f.Forecast.RecordPaycheckAsync(f.Market.Id, predicted.Period, predicted.NetCents, first.Id);
        await transactions.DeleteAsync(first.Id);
        Assert.Null((await f.Forecast.GetPaychecksAsync(day, day)).Single().Recorded);

        var second = await transactions.CreateAsync(new TransactionInput(TransactionType.Income, 16000, day, account.Id));
        await f.Forecast.RecordPaycheckAsync(f.Market.Id, predicted.Period, predicted.NetCents, second.Id);
        Assert.Equal(new RecordedPaycheck(second.Id, 16000), (await f.Forecast.GetPaychecksAsync(day, day)).Single().Recorded);
        await using var db = f.Db.Context();
        Assert.Equal(1, await db.PaycheckRecords.IgnoreQueryFilters().CountAsync()); // reused, not duplicated
    }

    [Fact]
    public async Task Only_income_can_be_recorded_as_a_paycheck()
    {
        await using var f = await CreateAsync();
        await AddYourScheduleAsync(f);
        var account = await f.Db.AddAccountAsync("Checking", AccountType.Checking);
        var expense = await f.Db.AddTransactionAsync(new Transaction { AccountId = account.Id, AmountCents = 100, TransactionType = TransactionType.Expense, Date = Today });
        var p = (await f.Forecast.GetPaychecksAsync(new DateOnly(2026, 10, 9), new DateOnly(2026, 10, 9))).Single();

        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Forecast.RecordPaycheckAsync(f.Market.Id, p.Period, p.NetCents, expense.Id));
    }

    // ---- Jobs and shifts ----------------------------------------------------------------------

    [Theory]
    [InlineData("", 1350)]
    [InlineData("Riverside Market", 0)]
    [InlineData("Riverside Market", IncomeService.MaxHourlyRateCents + 1)]
    public async Task Job_validation(string name, long rate)
    {
        await using var f = await CreateAsync();
        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Income.CreateJobAsync(MarketInput(rate) with { Name = name }));
    }

    [Fact]
    public async Task Job_rejects_impossible_local_tax_settings()
    {
        await using var f = await CreateAsync();
        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Income.CreateJobAsync(MarketInput() with { WorkLstAnnualCents = 5300 }));
        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Income.CreateJobAsync(MarketInput() with { WorkNonResidentEitBasisPoints = 1001 }));
        await Assert.ThrowsAsync<LedgerValidationException>(() => f.Income.CreateJobAsync(MarketInput() with { PaydayOffsetDays = 40 }));
    }

    [Theory]
    [InlineData(16, 0, 20, 0, 240)]
    [InlineData(11, 0, 17, 30, 390)]
    [InlineData(22, 0, 6, 0, 480)]    // overnight
    public void Shift_length_from_times(int sh, int sm, int eh, int em, int expected) =>
        Assert.Equal(expected, IncomeService.ScheduledMinutesFor(new ShiftInput(1, Today, null, new(sh, sm), new(eh, em), null, ShiftStatus.Scheduled, null)));

    [Fact]
    public void Shift_needs_hours_or_times()
    {
        Assert.Throws<LedgerValidationException>(() => IncomeService.ScheduledMinutesFor(new ShiftInput(1, Today, null, null, null, null, ShiftStatus.Scheduled, null)));
        Assert.Throws<LedgerValidationException>(() => IncomeService.ScheduledMinutesFor(new ShiftInput(1, Today, 0, null, null, null, ShiftStatus.Scheduled, null)));
        Assert.Throws<LedgerValidationException>(() => IncomeService.ScheduledMinutesFor(new ShiftInput(1, Today, 1500, null, null, null, ShiftStatus.Scheduled, null)));
    }

    [Fact]
    public async Task A_shift_must_be_longer_than_its_unpaid_break()
    {
        await using var f = await CreateAsync();
        await Assert.ThrowsAsync<LedgerValidationException>(() => f.AddAsync(10, 1, 30, breakOverride: 30));
        await Assert.ThrowsAsync<LedgerValidationException>(() => f.AddAsync(10, 1, 60, breakOverride: -5));
    }

    [Fact]
    public async Task Changing_the_job_break_rule_reprices_existing_shifts()
    {
        await using var f = await CreateAsync();
        await f.AddAsync(10, 5, 360);
        async Task<long> Gross() => (await f.Forecast.GetPaychecksAsync(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31))).Single().GrossCents;

        Assert.Equal(330 * 1350 / 60, await Gross());   // 5.5h paid
        await f.Income.UpdateJobAsync(f.Market.Id, MarketInput() with { UnpaidBreakMinutes = 0 });
        Assert.Equal(360 * 1350 / 60, await Gross());   // 6h paid
    }

    [Fact]
    public async Task Pasting_again_replaces_scheduled_shifts_but_keeps_worked_ones()
    {
        await using var f = await CreateAsync();
        await f.AddAsync(9, 24, 240, ShiftStatus.Worked);
        var first = ScheduleParser.Parse("9/24: 4hrs, 29th: 4hrs, 30th: 4hrs", Today);
        await f.Income.SavePastedShiftsAsync(f.Market.Id, first.Shifts);

        var updated = ScheduleParser.Parse("29th: 5hrs, 30th: 4hrs", Today);
        var (added, replaced) = await f.Income.SavePastedShiftsAsync(f.Market.Id, updated.Shifts);

        Assert.Equal((2, 2), (added, replaced));
        var shifts = await f.Income.GetShiftsAsync(new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 31));
        // Worked training shift kept; the pasted 9/24 duplicate stays too (it was scheduled, not re-pasted).
        Assert.Equal([240, 240, 300, 240], shifts.Select(s => s.ScheduledMinutes));
        Assert.Single(shifts, s => s.Status == ShiftStatus.Worked);
    }
}

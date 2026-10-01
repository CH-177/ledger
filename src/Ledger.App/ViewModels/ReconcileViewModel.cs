using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Ledger.App.Services;
using Ledger.Core;
using Ledger.Core.Models;
using Ledger.Core.Services;
using Microsoft.Extensions.Logging;

namespace Ledger.App.ViewModels;

/// <summary>One transaction to tick off against the statement.</summary>
public sealed partial class ReconcileRowViewModel(ReconcileItem item) : ObservableObject
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");
    public ReconcileItem Item { get; } = item;
    [ObservableProperty] public partial bool IsTicked { get; set; }
    public string DateText => Item.Date.ToString("MMM d", Culture);
    public string Title => Item.Title;
    public string AmountText => Money.Format(Item.EffectCents, MoneySign.Always);
    public bool IsMoneyIn => Item.EffectCents > 0;
    public bool IsMoneyOut => !IsMoneyIn;
    public string? Badge => Item.AfterStatement ? "After the statement date" : Item.IsPending ? "Pending" : null;
    public bool HasBadge => Badge is not null;
    public string TickName => $"On the statement: {Title}, {AmountText}, {DateText}";
}

/// <summary>A past statement in the history list.</summary>
public sealed record ReconciledStatementViewModel(string Text, bool CanUndo);

/// <summary>
/// Reconcile one account with a statement: enter its date and ending balance, tick what's on it,
/// and finish when the difference is $0.00.
/// </summary>
public sealed partial class ReconcileViewModel(
    ReconciliationService reconciliations,
    AccountService accounts,
    NavigationService navigation,
    TimeProvider time,
    ILogger<ReconcileViewModel> logger) : ObservableObject
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");
    private ReconcileStart? _start;
    private int _accountId;
    private bool _loading;

    public ObservableCollection<FilterOption<int?>> AccountOptions { get; } = [];
    public ObservableCollection<ReconcileRowViewModel> Rows { get; } = [];
    public ObservableCollection<string> Hints { get; } = [];
    public ObservableCollection<ReconciledStatementViewModel> History { get; } = [];

    [ObservableProperty] public partial int AccountIndex { get; set; } = -1;
    [ObservableProperty] public partial DateTimeOffset? StatementDate { get; set; }
    [ObservableProperty] public partial string StatementBalanceText { get; set; } = "";
    [ObservableProperty] public partial string BalanceLabel { get; set; } = "Statement ending balance";
    [ObservableProperty] public partial string ClearedText { get; set; } = "";
    [ObservableProperty] public partial string DifferenceText { get; set; } = "";
    [ObservableProperty] public partial bool IsBalanced { get; set; }
    [ObservableProperty] public partial bool IsOff { get; set; }
    [ObservableProperty] public partial bool HasRows { get; set; }
    [ObservableProperty] public partial bool HasHints { get; set; }
    [ObservableProperty] public partial bool HasHistory { get; set; }
    [ObservableProperty] public partial string EmptyText { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] public partial string? ErrorMessage { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasResult))] public partial string? ResultText { get; set; }
    public bool HasError => ErrorMessage is not null;
    public bool HasResult => ResultText is not null;

    public async Task LoadAsync(int? accountId)
    {
        _loading = true;
        try
        {
            AccountOptions.Clear();
            foreach (var b in await accounts.GetAccountBalancesAsync())
            {
                AccountOptions.Add(new(b.Account.Name, b.Account.Id));
            }
            AccountIndex = Math.Max(0, AccountOptions.ToList().FindIndex(o => o.Value == accountId));
            // Most statements end on the last day of a month: default to last month's.
            var today = DateOnly.FromDateTime(time.GetLocalNow().Date);
            var lastMonthEnd = new DateOnly(today.Year, today.Month, 1).AddDays(-1);
            StatementDate = new DateTimeOffset(lastMonthEnd.ToDateTime(TimeOnly.MinValue));
            StatementBalanceText = "";
            ResultText = null;
        }
        finally
        {
            _loading = false;
        }
        await RefreshAsync();
    }

    partial void OnAccountIndexChanged(int value) { if (!_loading) { StatementBalanceText = ""; _ = RefreshAsync(); } }
    partial void OnStatementDateChanged(DateTimeOffset? value) { if (!_loading) _ = RefreshAsync(); }
    partial void OnStatementBalanceTextChanged(string value) => Recalculate();

    private async Task RefreshAsync()
    {
        ErrorMessage = null;
        foreach (var r in Rows) r.PropertyChanged -= OnRowChanged;
        Rows.Clear();
        _start = null;
        if (AccountOptions.ElementAtOrDefault(AccountIndex)?.Value is not { } accountId || StatementDate is not { } date)
        {
            Recalculate();
            return;
        }
        _accountId = accountId;
        try
        {
            _start = await reconciliations.StartAsync(accountId, DateOnly.FromDateTime(date.Date));
            BalanceLabel = _start.IsLiability ? "Statement balance (amount owed)" : "Statement ending balance";
            var ticked = _start.DefaultTicked;
            foreach (var item in _start.Items)
            {
                var row = new ReconcileRowViewModel(item) { IsTicked = ticked.Contains(item.TransactionId) };
                row.PropertyChanged += OnRowChanged;
                Rows.Add(row);
            }
            HasRows = Rows.Count > 0;
            EmptyText = HasRows ? "" : "Every transaction up to this date is already reconciled.";
            await LoadHistoryAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Starting a reconciliation failed");
            ErrorMessage = "Ledger couldn't load this account's transactions.";
        }
        Recalculate();
    }

    private async Task LoadHistoryAsync()
    {
        History.Clear();
        var history = await reconciliations.GetHistoryAsync(_accountId);
        for (var i = 0; i < history.Count && i < 6; i++)
        {
            var r = history[i];
            var balance = _start?.IsLiability == true ? $"{Money.Format(-r.StatementBalanceCents)} owed" : Money.Format(r.StatementBalanceCents);
            History.Add(new ReconciledStatementViewModel(
                $"Statement ending {r.StatementDate.ToString("MMM d, yyyy", Culture)}: {balance}, {r.TransactionCount} transaction{(r.TransactionCount == 1 ? "" : "s")}", i == 0));
        }
        HasHistory = History.Count > 0;
    }

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReconcileRowViewModel.IsTicked)) Recalculate();
    }

    private HashSet<int> Ticked => Rows.Where(r => r.IsTicked).Select(r => r.Item.TransactionId).ToHashSet();

    /// <summary>The statement balance as stored (a card's amount owed is negative), or null while not a number.</summary>
    private long? StatementCents =>
        _start is not null && Money.TryParse(StatementBalanceText, out var entered) ? (_start.IsLiability ? -entered : entered) : null;

    private void Recalculate()
    {
        Hints.Clear();
        IsBalanced = IsOff = HasHints = false;
        if (_start is null)
        {
            ClearedText = DifferenceText = "";
            return;
        }
        var cleared = _start.ClearedBalanceCents(Ticked);
        ClearedText = _start.IsLiability
            ? $"Ticked transactions leave {Money.Format(-cleared)} owed"
            : $"Ticked transactions leave a balance of {Money.Format(cleared)}";
        if (StatementCents is not { } statement)
        {
            DifferenceText = "Enter the statement's balance to see the difference.";
            return;
        }
        var diff = _start.DifferenceCents(statement, Ticked);
        IsBalanced = diff == 0;
        IsOff = !IsBalanced;
        DifferenceText = IsBalanced ? "Difference: $0.00. It matches the statement." : $"Difference: {Money.Format(Math.Abs(diff))}";
        foreach (var hint in ReconciliationService.Hints(_start, statement, Ticked)) Hints.Add(hint);
        HasHints = Hints.Count > 0;
    }

    public void SetAll(bool ticked)
    {
        foreach (var r in Rows.Where(r => !r.Item.AfterStatement)) r.IsTicked = ticked;
    }

    public async Task FinishAsync()
    {
        if (_start is null || StatementCents is not { } statement || StatementDate is not { } date) return;
        ErrorMessage = null;
        try
        {
            var ticked = Ticked;
            await reconciliations.FinishAsync(_accountId, DateOnly.FromDateTime(date.Date), statement, ticked);
            ResultText = $"Reconciled: {ticked.Count} transaction{(ticked.Count == 1 ? "" : "s")} now match your statement ending {DateOnly.FromDateTime(date.Date).ToString("MMM d", Culture)}.";
            StatementBalanceText = "";
            await RefreshAsync();
        }
        catch (LedgerValidationException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Finishing a reconciliation failed");
            ErrorMessage = "Something went wrong, so nothing was marked reconciled.";
        }
    }

    public async Task UndoLastAsync()
    {
        ErrorMessage = null;
        try
        {
            await reconciliations.UndoLastAsync(_accountId);
            ResultText = "The latest reconciliation was undone. Its transactions are no longer marked reconciled.";
            await RefreshAsync();
        }
        catch (LedgerValidationException ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    public void GoToAccounts() => navigation.Navigate(AppPage.Accounts);
}

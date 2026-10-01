using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ledger.Core;
using Ledger.Core.Models;
using Ledger.Core.Services;
using Microsoft.Extensions.Logging;

namespace Ledger.App.ViewModels;

/// <summary>A labelled choice in a filter box. A null value means "all".</summary>
public sealed record FilterOption<T>(string Label, T? Value)
{
    public override string ToString() => Label;
}

/// <summary>One transaction in the list.</summary>
public sealed class TransactionRowViewModel(TransactionListItem item, int? viewedAccountId)
{
    public TransactionListItem Item { get; } = item;
    public int Id => Item.Id;

    public string Title => Item.Payee ?? Item.CategoryName ?? Item.Type switch
    {
        TransactionType.Transfer or TransactionType.OutsideIn or TransactionType.OutsideOut => "Transfer",
        TransactionType.Income => "Income",
        TransactionType.Refund => "Refund",
        _ => "Spending",
    };

    public string Subtitle
    {
        get
        {
            var where = Item.Type switch
            {
                TransactionType.Transfer => $"{Item.AccountName} → {Item.TransferAccountName}",
                TransactionType.OutsideIn => $"Someone else → {Item.AccountName}",
                TransactionType.OutsideOut => $"{Item.AccountName} → someone else",
                _ => Item.AccountName,
            };
            var isTransfer = Item.Type is TransactionType.Transfer or TransactionType.OutsideIn or TransactionType.OutsideOut;
            var category = Item.CategoryFullName ?? (isTransfer ? null : "No category");
            var text = category is null ? where : $"{category}  ·  {where}";
            return Item.Type == TransactionType.Refund ? $"Refund  ·  {text}" : text;
        }
    }

    private long? Signed => Item.SignedAmountFor(viewedAccountId);

    /// <summary>"+$184.72", "-$8.42", or unsigned for a transfer seen from no particular account.</summary>
    public string AmountText => Signed is { } s ? Money.Format(s, MoneySign.Always) : Money.Format(Item.AmountCents);

    /// <summary>Money arriving is shown in the positive colour; everything else stays neutral.</summary>
    public bool IsMoneyIn => Signed > 0;
    public bool IsNotMoneyIn => !IsMoneyIn;

    public bool IsPending => Item.Status == TransactionStatus.Pending;
    public bool IsAutoRecorded => Item.IsAutoRecorded;
    public string? Memo => Item.Memo;
    public bool HasMemo => !string.IsNullOrEmpty(Item.Memo);

    public string MoreOptionsName => $"More options for {Title}";

    public override string ToString() =>
        $"{Title}, {AmountText}, {Subtitle}" + (IsPending ? ", pending" : "") + (IsAutoRecorded ? ", recorded automatically" : "") + (HasMemo ? $", {Memo}" : "");
}

/// <summary>Transactions sharing a date, shown under a date heading.</summary>
public sealed class TransactionDayGroup(DateOnly date, IEnumerable<TransactionRowViewModel> rows) : List<TransactionRowViewModel>(rows)
{
    public string Header { get; } = date.ToString("dddd, MMMM d, yyyy", CultureInfo.GetCultureInfo("en-US"));
}

public sealed partial class TransactionsViewModel(
    TransactionService transactions,
    AccountService accounts,
    CategoryService categories,
    TimeProvider time,
    ILogger<TransactionsViewModel> logger) : ObservableObject
{
    private CancellationTokenSource? _searchDelay;
    private bool _suspendReload;

    public ObservableCollection<TransactionDayGroup> Groups { get; } = [];

    public ObservableCollection<FilterOption<int?>> AccountOptions { get; } = [];
    public ObservableCollection<FilterOption<int?>> CategoryOptions { get; } = [];

    public IReadOnlyList<FilterOption<TransactionType?>> TypeOptions { get; } =
    [
        new("All types", null),
        new("Spending", TransactionType.Expense),
        new("Income", TransactionType.Income),
        new("Transfers", TransactionType.Transfer),
        new("Refunds", TransactionType.Refund),
    ];

    public IReadOnlyList<string> DateOptions { get; } =
        ["All dates", "This month", "Last month", "Last 3 months", "This year", "Custom range"];

    private const int CustomDateIndex = 5;

    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial int AccountIndex { get; set; }
    [ObservableProperty] public partial int CategoryIndex { get; set; }
    [ObservableProperty] public partial int TypeIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomRange))]
    public partial int DateIndex { get; set; }

    [ObservableProperty] public partial DateTimeOffset? CustomFrom { get; set; }
    [ObservableProperty] public partial DateTimeOffset? CustomTo { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoTransactionsYet), nameof(ShowNoMatches), nameof(ShowList), nameof(ShowFirstTransactionPrompt), nameof(ShowNeedsAccountPrompt))]
    public partial bool IsLoaded { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoTransactionsYet), nameof(ShowNoMatches), nameof(ShowList), nameof(ShowFirstTransactionPrompt), nameof(ShowNeedsAccountPrompt))]
    public partial bool HasAnyTransactions { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoMatches), nameof(ShowList))]
    public partial int ResultCount { get; set; }

    [ObservableProperty] public partial string SummaryText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    /// <summary>The most recently deleted transaction, offered for undo.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUndo))]
    public partial TransactionRowViewModel? LastDeleted { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFirstTransactionPrompt), nameof(ShowNeedsAccountPrompt))]
    public partial bool HasAccounts { get; set; }

    public bool ShowFirstTransactionPrompt => ShowNoTransactionsYet && HasAccounts;
    public bool ShowNeedsAccountPrompt => ShowNoTransactionsYet && !HasAccounts;

    public bool HasError => ErrorMessage is not null;
    public bool HasUndo => LastDeleted is not null;
    public string UndoMessage => LastDeleted is null ? "" : $"Deleted “{LastDeleted.Title}” ({LastDeleted.AmountText}).";
    public bool IsCustomRange => DateIndex == CustomDateIndex;
    public bool ShowNoTransactionsYet => IsLoaded && !HasAnyTransactions;
    public bool ShowNoMatches => IsLoaded && HasAnyTransactions && ResultCount == 0;
    public bool ShowList => IsLoaded && ResultCount > 0;

    private int? SelectedAccountId => AccountOptions.ElementAtOrDefault(AccountIndex)?.Value;

    /// <summary>Loads filter choices, applies an optional starting account filter, then the list.</summary>
    public async Task InitializeAsync(int? accountId)
    {
        _suspendReload = true;
        try
        {
            var accountList = await accounts.GetAccountBalancesAsync(includeArchived: true);
            HasAccounts = accountList.Any(a => !a.Account.IsArchived);
            AccountOptions.Clear();
            AccountOptions.Add(new("All accounts", null));
            foreach (var a in accountList)
            {
                AccountOptions.Add(new(a.Account.IsArchived ? $"{a.Account.Name} (archived)" : a.Account.Name, a.Account.Id));
            }

            CategoryOptions.Clear();
            CategoryOptions.Add(new("All categories", null));
            foreach (var node in await categories.GetTreeAsync(includeArchived: true))
            {
                CategoryOptions.Add(new(node.Category.Name, node.Category.Id));
                foreach (var child in node.Children)
                {
                    CategoryOptions.Add(new($"{node.Category.Name} › {child.Name}", child.Id));
                }
            }

            AccountIndex = Math.Max(0, AccountOptions.ToList().FindIndex(o => o.Value == accountId));
            CategoryIndex = 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load transaction filters");
            ErrorMessage = "Something went wrong while loading your transactions. Your existing data has not been changed.";
        }
        finally
        {
            _suspendReload = false;
        }
        await LoadAsync();
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        ErrorMessage = null;
        try
        {
            var query = BuildQuery();
            var items = await transactions.QueryAsync(query);
            var totals = await transactions.GetTotalsAsync(query);
            HasAnyTransactions = items.Count > 0 || (await transactions.GetTotalsAsync(new TransactionQuery())).Count > 0;

            var viewed = SelectedAccountId;
            Groups.Clear();
            foreach (var day in items.GroupBy(i => i.Date))
            {
                Groups.Add(new TransactionDayGroup(day.Key, day.Select(i => new TransactionRowViewModel(i, viewed))));
            }

            ResultCount = items.Count;
            SummaryText = items.Count == 0 ? "" :
                $"{items.Count} {(items.Count == 1 ? "transaction" : "transactions")}  ·  " +
                $"Income {Money.Format(totals.IncomeCents)}  ·  Spending {Money.Format(totals.ExpenseCents)}" +
                (totals.RefundCents > 0 ? $" (after {Money.Format(totals.RefundCents)} in refunds)" : "");
            IsLoaded = true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load transactions");
            ErrorMessage = "Something went wrong while loading your transactions. Your existing data has not been changed.";
        }
    }

    [RelayCommand]
    private async Task ClearFiltersAsync()
    {
        _suspendReload = true;
        SearchText = "";
        AccountIndex = 0;
        CategoryIndex = 0;
        TypeIndex = 0;
        DateIndex = 0;
        CustomFrom = null;
        CustomTo = null;
        _suspendReload = false;
        await LoadAsync();
    }

    public async Task DeleteAsync(TransactionRowViewModel row)
    {
        ErrorMessage = null;
        try
        {
            await transactions.DeleteAsync(row.Id);
            LastDeleted = row;
            OnPropertyChanged(nameof(UndoMessage));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete transaction {Id}", row.Id);
            ErrorMessage = "Something went wrong while deleting this transaction. Your existing data has not been changed.";
            return;
        }
        await LoadAsync();
    }

    [RelayCommand]
    private async Task UndoDeleteAsync()
    {
        if (LastDeleted is not { } row)
        {
            return;
        }
        try
        {
            await transactions.RestoreAsync(row.Id);
            LastDeleted = null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to restore transaction {Id}", row.Id);
            ErrorMessage = "Something went wrong while restoring this transaction.";
            return;
        }
        await LoadAsync();
    }

    public void DismissUndo() => LastDeleted = null;

    // Filter changes reload the list. Search waits briefly so typing doesn't query on every key.
    partial void OnSearchTextChanged(string value) => _ = ReloadAfterDelayAsync();
    partial void OnAccountIndexChanged(int value) => ReloadNow();
    partial void OnCategoryIndexChanged(int value) => ReloadNow();
    partial void OnTypeIndexChanged(int value) => ReloadNow();
    partial void OnDateIndexChanged(int value) => ReloadNow();
    partial void OnCustomFromChanged(DateTimeOffset? value) => ReloadNow();
    partial void OnCustomToChanged(DateTimeOffset? value) => ReloadNow();

    private void ReloadNow()
    {
        if (!_suspendReload)
        {
            _ = LoadAsync();
        }
    }

    private async Task ReloadAfterDelayAsync()
    {
        if (_suspendReload)
        {
            return;
        }
        _searchDelay?.Cancel();
        var cts = _searchDelay = new CancellationTokenSource();
        try
        {
            await Task.Delay(250, cts.Token);
            await LoadAsync();
        }
        catch (TaskCanceledException)
        {
        }
    }

    private TransactionQuery BuildQuery()
    {
        var (from, to) = DateRange();
        return new TransactionQuery(
            Search: string.IsNullOrWhiteSpace(SearchText) ? null : SearchText,
            From: from,
            To: to,
            AccountId: SelectedAccountId,
            CategoryId: CategoryOptions.ElementAtOrDefault(CategoryIndex)?.Value,
            Type: TypeOptions.ElementAtOrDefault(TypeIndex)?.Value);
    }

    private (DateOnly? From, DateOnly? To) DateRange()
    {
        var today = DateOnly.FromDateTime(time.GetLocalNow().DateTime);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        return DateIndex switch
        {
            1 => (monthStart, monthStart.AddMonths(1).AddDays(-1)),
            2 => (monthStart.AddMonths(-1), monthStart.AddDays(-1)),
            3 => (monthStart.AddMonths(-2), monthStart.AddMonths(1).AddDays(-1)),
            4 => (new DateOnly(today.Year, 1, 1), new DateOnly(today.Year, 12, 31)),
            CustomDateIndex => (
                CustomFrom is { } f ? DateOnly.FromDateTime(f.Date) : null,
                CustomTo is { } t ? DateOnly.FromDateTime(t.Date) : null),
            _ => (null, null),
        };
    }
}

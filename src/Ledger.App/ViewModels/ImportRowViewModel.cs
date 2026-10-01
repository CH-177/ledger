using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Ledger.Core;
using Ledger.Core.Import;
using Ledger.Core.Models;

namespace Ledger.App.ViewModels;

/// <summary>
/// One bank row in review. The user can untick it, correct the payee, change what it is and file it
/// in a category. Types follow the money: money out is spending or a transfer; money in is income or
/// a transfer.
/// </summary>
public sealed partial class ImportRowViewModel : ObservableObject
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");
    private readonly IReadOnlyList<FilterOption<int?>> _spending;
    private readonly IReadOnlyList<FilterOption<int?>> _income;

    public ImportCandidate Candidate { get; }
    public bool IsMoneyIn => Candidate.Row.AmountCents > 0;
    public bool IsMoneyOut => !IsMoneyIn;

    public IReadOnlyList<string> TypeOptions { get; }
    private readonly TransactionType[] _types;
    public IReadOnlyList<FilterOption<int?>> TransferOptions { get; }

    [ObservableProperty] public partial bool Include { get; set; }
    [ObservableProperty] public partial string Payee { get; set; }
    [ObservableProperty] public partial bool Remember { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsTransfer), nameof(IsNotTransfer), nameof(CategoryOptions), nameof(HasBill), nameof(HasPaycheck))]
    public partial int TypeIndex { get; set; }

    /// <summary>Whether this row counts as the payment of the bill it matched.</summary>
    [ObservableProperty] public partial bool LinkBill { get; set; }

    /// <summary>Whether this deposit counts as the predicted paycheck it matched.</summary>
    [ObservableProperty] public partial bool LinkPaycheck { get; set; }

    [ObservableProperty] public partial int CategoryIndex { get; set; }
    [ObservableProperty] public partial int TransferIndex { get; set; }

    public ImportRowViewModel(ImportCandidate candidate, IReadOnlyList<FilterOption<int?>> spending, IReadOnlyList<FilterOption<int?>> income,
        IReadOnlyList<FilterOption<int?>> otherAccounts)
    {
        Candidate = candidate;
        _spending = spending;
        _income = income;
        TransferOptions = otherAccounts;
        (TypeOptions, _types) = IsMoneyIn
            ? (["Income", "Refund", "Transfer in"], [TransactionType.Income, TransactionType.Refund, TransactionType.Transfer])
            : ((IReadOnlyList<string>)["Spending", "Transfer out"], new[] { TransactionType.Expense, TransactionType.Transfer });
        Include = candidate.IncludeByDefault;
        Payee = candidate.Payee;
        var outside = candidate.Type is TransactionType.OutsideIn or TransactionType.OutsideOut;
        TypeIndex = Math.Max(0, Array.IndexOf(_types, outside ? TransactionType.Transfer : candidate.Type));
        LinkBill = candidate.Bill is not null;
        LinkPaycheck = candidate.Paycheck is not null;
        CategoryIndex = Math.Max(0, CategoryOptions.ToList().FindIndex(o => o.Value == candidate.CategoryId));
        TransferIndex = outside
            ? otherAccounts.Count - 1
            : Math.Max(0, otherAccounts.ToList().FindIndex(o => o.Value == candidate.TransferAccountId && o.Value is not null));
    }

    private TransactionType SelectedType => _types[Math.Clamp(TypeIndex, 0, _types.Length - 1)];
    public bool IsTransfer => SelectedType == TransactionType.Transfer;
    public bool IsNotTransfer => !IsTransfer;
    public IReadOnlyList<FilterOption<int?>> CategoryOptions => SelectedType switch
    {
        TransactionType.Transfer => [new("No category", null)],
        TransactionType.Income => _income,
        _ => _spending, // spending and refunds
    };

    /// <summary>The row matched a bill and is still the kind of transaction that bill is.</summary>
    public bool HasBill => Candidate.Bill is { } b && b.Type == SelectedType;
    public bool HasPaycheck => Candidate.Paycheck is not null && SelectedType == TransactionType.Income;
    public string PaycheckText => Candidate.Paycheck is { } p
        ? $"Counts as your {p.JobName} paycheck for {p.Period.PayDate.ToString("MMM d", Culture)} (Ledger estimated {Money.Format(p.EstimatedNetCents)})"
        : "";
    public string BillText => Candidate.Bill is { } b
        ? $"Counts as the “{b.Name}” {(b.Type == TransactionType.Income ? "income" : "bill")} due {b.Date.ToString("MMM d", Culture)}"
        : "";

    private int? _categoryId;

    partial void OnCategoryIndexChanged(int value)
    {
        if (value >= 0 && value < CategoryOptions.Count) _categoryId = CategoryOptions[value].Value;
    }

    // Keep the category when it still fits (spending and refunds share spending categories).
    partial void OnTypeIndexChanged(int value) =>
        CategoryIndex = Math.Max(0, CategoryOptions.ToList().FindIndex(o => o.Value is not null && o.Value == _categoryId));

    public string DateText => Candidate.Row.Date.ToString("MMM d", Culture);
    public string AmountText => Money.Format(Candidate.Row.AmountCents, MoneySign.Always);
    public string Description => Candidate.Row.Description + (Candidate.Row.Memo is { } m ? $"  ·  {m}" : "");

    public bool IsDuplicate => Candidate.Duplicate != DuplicateKind.None;
    public bool NeedsAttention => Candidate.AttentionText is not null && !IsDuplicate;
    public string BadgeText => Candidate.DuplicateText ?? Candidate.AttentionText ?? "";
    public bool FromRule => Candidate.FromRememberedRule;
    public string IncludeName => $"Import {AmountText} on {DateText}, {Candidate.Row.Description}";

    /// <summary>The user's choices for this row, or a message saying what's missing.</summary>
    public (ImportDecision? Decision, string? Problem) ToDecision()
    {
        var type = SelectedType;
        int? transferAccount = IsTransfer ? TransferOptions.ElementAtOrDefault(TransferIndex)?.Value : null;
        if (transferAccount == TransactionEditorViewModel.OutsideAccount)
        {
            // Someone else's account: neither income nor spending, and no Ledger account on the other side.
            return (new ImportDecision(Candidate.Index, IsMoneyIn ? TransactionType.OutsideIn : TransactionType.OutsideOut, Payee, null, null, Remember), null);
        }
        if (IsTransfer && transferAccount is null)
        {
            return (null, $"{DateText} {AmountText}: choose the account the transfer is {(IsMoneyIn ? "from" : "to")}.");
        }
        return (new ImportDecision(Candidate.Index, type, Payee, IsTransfer ? null : CategoryOptions.ElementAtOrDefault(CategoryIndex)?.Value,
            transferAccount, Remember, HasBill && LinkBill ? Candidate.Bill : null, HasPaycheck && LinkPaycheck ? Candidate.Paycheck : null), null);
    }
}

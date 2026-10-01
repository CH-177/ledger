using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ledger.App.Presentation;
using Ledger.Core;
using Ledger.Core.Models;
using Ledger.Core.Services;
using Microsoft.Extensions.Logging;

namespace Ledger.App.ViewModels;

/// <summary>One account as shown in the list. All figures come from <see cref="AccountService"/>.</summary>
public sealed class AccountRowViewModel(AccountBalance source)
{
    public Account Account { get; } = source.Account;
    public long BalanceCents { get; } = source.BalanceCents;

    public string Name => Account.Name;
    public string Glyph => AccountTypeDisplay.For(Account.AccountType).Glyph;
    public string BalanceText => Money.Format(BalanceCents);

    public string Subtitle
    {
        get
        {
            var parts = new List<string> { AccountTypeDisplay.For(Account.AccountType).Name };
            if (!string.IsNullOrEmpty(Account.InstitutionName))
            {
                parts.Add(Account.InstitutionName);
            }
            if (!Account.IsIncludedInNetWorth)
            {
                parts.Add("Not in net worth");
            }
            return string.Join("  ·  ", parts);
        }
    }

    /// <summary>An asset account below zero. Shown with a text label, not colour alone.</summary>
    public bool IsOverdrawn => !Account.IsLiability && BalanceCents < 0;

    public string AutomationName => $"{Name}, {Subtitle}, balance {BalanceText}" + (IsOverdrawn ? ", overdrawn" : "");

    public string MoreOptionsName => $"More options for {Name}";

    /// <summary>List items are announced by screen readers using this.</summary>
    public override string ToString() => AutomationName;
}

public sealed partial class AccountsViewModel(AccountService accounts, ILogger<AccountsViewModel> logger)
    : ObservableObject
{
    public ObservableCollection<AccountRowViewModel> ActiveAccounts { get; } = [];
    public ObservableCollection<AccountRowViewModel> ArchivedAccounts { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState), nameof(HasActiveAccounts), nameof(HasArchivedAccounts))]
    public partial bool IsLoaded { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial string NetWorthText { get; set; } = "";

    [ObservableProperty]
    public partial string AssetsText { get; set; } = "";

    [ObservableProperty]
    public partial string LiabilitiesText { get; set; } = "";

    [ObservableProperty]
    public partial bool ShowArchived { get; set; }

    public bool HasError => ErrorMessage is not null;
    public bool HasActiveAccounts => IsLoaded && ActiveAccounts.Count > 0;
    public bool HasArchivedAccounts => IsLoaded && ArchivedAccounts.Count > 0;
    public bool ShowEmptyState => IsLoaded && ActiveAccounts.Count == 0;

    [RelayCommand]
    public async Task LoadAsync()
    {
        ErrorMessage = null;
        try
        {
            var balances = await accounts.GetAccountBalancesAsync(includeArchived: true);
            var worth = await accounts.GetNetWorthAsync();

            ActiveAccounts.Clear();
            ArchivedAccounts.Clear();
            foreach (var b in balances)
            {
                (b.Account.IsArchived ? ArchivedAccounts : ActiveAccounts).Add(new AccountRowViewModel(b));
            }

            NetWorthText = Money.Format(worth.NetWorthCents);
            AssetsText = Money.Format(worth.AssetsCents);
            LiabilitiesText = Money.Format(worth.LiabilitiesCents);

            // Re-raise even when already loaded so the computed visibility flags refresh.
            IsLoaded = false;
            IsLoaded = true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load accounts");
            ErrorMessage = "Something went wrong while loading your accounts. Your existing data has not been changed.";
        }
    }

    public async Task SetArchivedAsync(AccountRowViewModel row, bool archived)
    {
        ErrorMessage = null;
        try
        {
            await accounts.SetArchivedAsync(row.Account.Id, archived);
        }
        catch (LedgerValidationException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to change archive state for account {AccountId}", row.Account.Id);
            ErrorMessage = "Something went wrong while updating this account. Your existing data has not been changed.";
            return;
        }
        await LoadAsync();
    }
}

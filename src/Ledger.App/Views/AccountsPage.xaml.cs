using CommunityToolkit.Mvvm.Input;
using Ledger.App.Services;
using Ledger.App.ViewModels;
using Ledger.App.Views.Dialogs;
using Ledger.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Ledger.App.Views;

public sealed partial class AccountsPage : Page
{
    private readonly DialogService _dialogs = App.Services.GetRequiredService<DialogService>();

    public AccountsViewModel ViewModel { get; } = App.Services.GetRequiredService<AccountsViewModel>();

    public IAsyncRelayCommand AddAccountCommand { get; }

    public AccountsPage()
    {
        AddAccountCommand = new AsyncRelayCommand(AddAccountAsync);
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e) => await ViewModel.LoadAsync();

    private async Task AddAccountAsync()
    {
        var editor = App.Services.GetRequiredService<AccountEditorViewModel>();
        editor.LoadNew();
        await ShowEditorAsync(editor);
    }

    private async Task EditAccountAsync(AccountRowViewModel row)
    {
        var editor = App.Services.GetRequiredService<AccountEditorViewModel>();
        editor.LoadExisting(row.Account);
        await ShowEditorAsync(editor);
    }

    private async Task ShowEditorAsync(AccountEditorViewModel editor)
    {
        // The dialog only closes with Primary after a successful save.
        if (await _dialogs.ShowAsync(new AccountEditorDialog(editor)) == ContentDialogResult.Primary)
        {
            await ViewModel.LoadAsync();
        }
    }

    private async void OnAccountClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AccountRowViewModel row)
        {
            await EditAccountAsync(row);
        }
    }

    private async void OnEditClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AccountRowViewModel row)
        {
            await EditAccountAsync(row);
        }
    }

    private void OnViewTransactionsClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AccountRowViewModel row)
        {
            App.Services.GetRequiredService<NavigationService>().Navigate(AppPage.Transactions, row.Account.Id);
        }
    }

    private void OnReconcileClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AccountRowViewModel row)
        {
            App.Services.GetRequiredService<NavigationService>().Navigate(AppPage.Reconcile, row.Account.Id);
        }
    }

    private async void OnArchiveClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not AccountRowViewModel row)
        {
            return;
        }

        var message = "It will be hidden from everyday views. Its history is kept, and you can restore it at any time.";
        if (row.BalanceCents != 0)
        {
            var amount = row.Account.IsLiability && row.BalanceCents < 0
                ? $"{Money.Format(-row.BalanceCents)} owed"
                : $"a balance of {Money.Format(row.BalanceCents)}";
            message += $"\n\nIt still has {amount}, which will keep counting toward net worth.";
        }

        if (await _dialogs.ConfirmAsync($"Archive {row.Name}?", message, "Archive"))
        {
            await ViewModel.SetArchivedAsync(row, archived: true);
        }
    }

    private async void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AccountRowViewModel row)
        {
            await ViewModel.SetArchivedAsync(row, archived: false);
        }
    }
}

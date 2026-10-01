using CommunityToolkit.Mvvm.Input;
using Ledger.App.Services;
using Ledger.App.ViewModels;
using Ledger.App.Views.Dialogs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;

namespace Ledger.App.Views;

public sealed partial class TransactionsPage : Page
{
    private readonly DialogService _dialogs = App.Services.GetRequiredService<DialogService>();
    private readonly NavigationService _navigation = App.Services.GetRequiredService<NavigationService>();

    public TransactionsViewModel ViewModel { get; } = App.Services.GetRequiredService<TransactionsViewModel>();

    public IAsyncRelayCommand AddTransactionCommand { get; }
    public IRelayCommand GoToAccountsCommand { get; }

    public TransactionsPage()
    {
        AddTransactionCommand = new AsyncRelayCommand(() => ShowEditorAsync(null));
        GoToAccountsCommand = new RelayCommand(() => _navigation.Navigate(AppPage.Accounts));
        InitializeComponent();
    }

    /// <summary>The parameter, when given, is an account id to filter by (from the Accounts page).</summary>
    protected override async void OnNavigatedTo(NavigationEventArgs e) =>
        await ViewModel.InitializeAsync(e.Parameter as int?);

    private async void OnAddClick(object sender, RoutedEventArgs e) => await ShowEditorAsync(null);

    private void OnImportClick(object sender, RoutedEventArgs e) =>
        App.Services.GetRequiredService<Services.NavigationService>().Navigate(Services.AppPage.Import);

    private async void OnAddAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (ViewModel.HasAccounts)
        {
            await ShowEditorAsync(null);
        }
    }

    private void OnSearchAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SearchBox.Focus(FocusState.Keyboard);
    }

    private async void OnTransactionClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TransactionRowViewModel row)
        {
            await ShowEditorAsync(row.Id);
        }
    }

    private async void OnEditClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is TransactionRowViewModel row)
        {
            await ShowEditorAsync(row.Id);
        }
    }

    /// <summary>Deletes straight away and offers Undo, rather than asking first (except for a reconciled one).</summary>
    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not TransactionRowViewModel row) return;
        if (row.Item.Status == Ledger.Core.Models.TransactionStatus.Reconciled
            && !await App.Services.GetRequiredService<DialogService>().ConfirmAsync($"Delete “{row.Title}”?",
                "It's reconciled with a bank statement, so deleting it means that statement won't match any more.", "Delete"))
        {
            return;
        }
        await ViewModel.DeleteAsync(row);
    }

    private void OnUndoClosed(InfoBar sender, InfoBarClosedEventArgs args) => ViewModel.DismissUndo();

    private async Task ShowEditorAsync(int? transactionId)
    {
        var editor = App.Services.GetRequiredService<TransactionEditorViewModel>();
        try
        {
            if (transactionId is { } id)
            {
                await editor.LoadForEditAsync(id);
            }
            else
            {
                // When the list is filtered to an account, new entries default to it.
                await editor.LoadForCreateAsync(ViewModel.AccountOptions.ElementAtOrDefault(ViewModel.AccountIndex)?.Value);
            }
        }
        catch (Ledger.Core.LedgerValidationException)
        {
            // e.g. the transaction was deleted meanwhile: refresh so the list reflects it.
            await ViewModel.LoadAsync();
            return;
        }

        await _dialogs.ShowAsync(new TransactionEditorDialog(editor));
        if (editor.SavedAny)
        {
            await ViewModel.LoadAsync();
        }
    }
}

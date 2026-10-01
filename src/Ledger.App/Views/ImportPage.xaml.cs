using Ledger.App.Services;
using Ledger.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Ledger.App.Views;

public sealed partial class ImportPage : Page
{
    private readonly DialogService _dialogs = App.Services.GetRequiredService<DialogService>();

    public ImportViewModel ViewModel { get; } = App.Services.GetRequiredService<ImportViewModel>();

    public ImportPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs e) => await ViewModel.LoadAsync();

    private async void OnChooseFileClick(object sender, RoutedEventArgs e) => await ViewModel.ChooseFileAsync();
    private void OnCancelClick(object sender, RoutedEventArgs e) => ViewModel.Cancel();
    private void OnTickAllClick(object sender, RoutedEventArgs e) => ViewModel.SetAll(true);
    private void OnUntickAllClick(object sender, RoutedEventArgs e) => ViewModel.SetAll(false);
    private void OnBackClick(object sender, RoutedEventArgs e) => ViewModel.GoToTransactions();
    private void OnViewTransactionsClick(object sender, RoutedEventArgs e) => ViewModel.GoToTransactions();
    private async void OnImportClick(object sender, RoutedEventArgs e) => await ViewModel.ImportAsync();
    private async void OnMatchBankClick(object sender, RoutedEventArgs e) => await ViewModel.MatchBankBalanceAsync();

    private async void OnUndoClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is RecentImportViewModel item
            && await _dialogs.ConfirmAsync($"Undo the import of {item.Title}?",
                "Every transaction this import added is removed. Anything you entered yourself is not touched, and you can import the file again.",
                "Undo import"))
        {
            await ViewModel.UndoAsync(item.BatchId);
        }
    }
}

using Ledger.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace Ledger.App.Views;

public sealed partial class BudgetPage : Page
{
    public BudgetViewModel ViewModel { get; } = App.Services.GetRequiredService<BudgetViewModel>();

    public BudgetPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs e) => await ViewModel.InitializeAsync();

    /// <summary>Plans save when focus leaves the box (Tab, click elsewhere) or on Enter.</summary>
    private async void OnPlanLostFocus(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is BudgetLineViewModel line)
        {
            await ViewModel.CommitPlanAsync(line);
        }
    }

    private async void OnPlanKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && (sender as FrameworkElement)?.Tag is BudgetLineViewModel line)
        {
            e.Handled = true;
            await ViewModel.CommitPlanAsync(line);
        }
    }

    private async void OnSetAsideClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is FundRowViewModel row) await ViewModel.SetAsideAsync(row);
    }
}

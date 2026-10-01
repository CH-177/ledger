using Ledger.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Ledger.App.Views;

public sealed partial class OverviewPage : Page
{
    public OverviewViewModel ViewModel { get; } = App.Services.GetRequiredService<OverviewViewModel>();

    public OverviewPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs e) => await ViewModel.LoadAsync();

    public Visibility IsFalse(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    public string BudgetLinkText(bool hasBudget) => hasBudget ? "Budget" : "Plan this month";
    public string GoalsLinkText(bool hasGoals) => hasGoals ? "All goals" : "Add a goal";
}

using Ledger.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Ledger.App.Views;

public sealed partial class ReportsPage : Page
{
    public ReportsViewModel ViewModel { get; } = App.Services.GetRequiredService<ReportsViewModel>();

    public ReportsPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs e) => await ViewModel.LoadAsync();
}

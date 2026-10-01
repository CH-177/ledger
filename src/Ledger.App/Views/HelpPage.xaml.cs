using Ledger.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Ledger.App.Views;

public sealed partial class HelpPage : Page
{
    public HelpViewModel ViewModel { get; } = App.Services.GetRequiredService<HelpViewModel>();

    public HelpPage()
    {
        InitializeComponent();
        TopicsSource.Source = ViewModel.Groups;
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HelpViewModel.Selected))
            {
                TopicScroller.ChangeView(null, 0, null, disableAnimation: true);
                if (ViewModel.Selected is { } item)
                {
                    TopicList.ScrollIntoView(item);
                }
            }
        };
    }

    /// <summary>The parameter, when there is one, is the topic to open (F1 passes the current page's).</summary>
    protected override void OnNavigatedTo(NavigationEventArgs e) => ViewModel.Load(e.Parameter as string);

    private void OnRelatedClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id })
        {
            ViewModel.Open(id);
        }
    }
}

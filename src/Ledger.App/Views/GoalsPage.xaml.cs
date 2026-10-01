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

public sealed partial class GoalsPage : Page
{
    private readonly DialogService _dialogs = App.Services.GetRequiredService<DialogService>();

    public GoalsViewModel ViewModel { get; } = App.Services.GetRequiredService<GoalsViewModel>();

    public IAsyncRelayCommand AddCommand { get; }

    public GoalsPage()
    {
        AddCommand = new AsyncRelayCommand(() => EditAsync(null));
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e) => await ViewModel.LoadAsync();

    private static GoalCardViewModel? CardOf(object sender) => (sender as FrameworkElement)?.Tag as GoalCardViewModel;

    private async Task EditAsync(int? goalId)
    {
        var editor = App.Services.GetRequiredService<GoalEditorViewModel>();
        try
        {
            if (goalId is { } id)
            {
                await editor.LoadForEditAsync(id);
            }
            else
            {
                await editor.LoadForCreateAsync();
            }
        }
        catch (LedgerValidationException)
        {
            await ViewModel.LoadAsync();
            return;
        }
        if (await _dialogs.ShowAsync(new GoalEditorDialog(editor)) == ContentDialogResult.Primary)
        {
            await ViewModel.LoadAsync();
        }
    }

    private async void OnEditClick(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { } card) await EditAsync(card.Goal.Id);
    }

    private async void OnAddMoneyClick(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { } card) await MoveMoneyAsync(card, takeOut: false);
    }

    private async void OnTakeOutClick(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { } card) await MoveMoneyAsync(card, takeOut: true);
    }

    private async Task MoveMoneyAsync(GoalCardViewModel card, bool takeOut)
    {
        var money = App.Services.GetRequiredService<GoalMoneyViewModel>();
        money.Load(card.Progress, takeOut);
        if (await _dialogs.ShowAsync(new GoalMoneyDialog(money)) == ContentDialogResult.Primary)
        {
            await ViewModel.LoadAsync();
        }
    }

    private async void OnHistoryClick(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is not { } card) return;
        var history = App.Services.GetRequiredService<GoalHistoryViewModel>();
        await history.LoadAsync(card.Progress);
        await _dialogs.ShowAsync(new GoalHistoryDialog(history));
        if (history.Changed)
        {
            await ViewModel.LoadAsync();
        }
    }

    private async void OnArchiveClick(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { } card) await ViewModel.ToggleArchivedAsync(card);
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { } card
            && await _dialogs.ConfirmAsync($"Delete {card.Name}?",
                "The goal and its history of amounts added are removed. Your accounts and transactions are not affected. To keep it out of the way instead, archive it.",
                "Delete"))
        {
            await ViewModel.DeleteAsync(card);
        }
    }
}

using Ledger.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ledger.App.Views.Dialogs;

public sealed partial class ShiftEditorDialog : ContentDialog
{
    public ShiftEditorViewModel ViewModel { get; }

    public ShiftEditorDialog(ShiftEditorViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Opened += (_, _) =>
        {
            if (ViewModel.UsesHours)
            {
                HoursBox.Focus(FocusState.Programmatic);
            }
        };
    }

    private async void OnSaveClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            args.Cancel = !await ViewModel.SaveAsync();
        }
        finally
        {
            deferral.Complete();
        }
    }
}

using Ledger.App.ViewModels;
using Ledger.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ledger.App.Views.Dialogs;

public sealed partial class TransactionEditorDialog : ContentDialog
{
    public TransactionEditorViewModel ViewModel { get; }

    /// <summary>"Save and add another" only makes sense when adding.</summary>
    public string SecondaryText => ViewModel.IsNew && ViewModel.AllowAddAnother ? "Save and add another" : "";

    public TransactionEditorDialog(TransactionEditorViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Opened += (_, _) => FocusAmount();
    }

    public bool IsNotSaving(bool isSaving) => !isSaving;

    private void FocusAmount()
    {
        AmountBox.Focus(FocusState.Programmatic);
        AmountBox.SelectAll();
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

    /// <summary>Saves, then keeps the dialog open with a fresh entry.</summary>
    private async void OnSaveAndAddClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            args.Cancel = true;
            if (await ViewModel.SaveAsync())
            {
                ViewModel.PrepareNext();
                FocusAmount();
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void OnPayeeTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            await ViewModel.UpdatePayeeSuggestionsAsync(sender.Text);
        }
    }

    private async void OnPayeeChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is PayeeSuggestion suggestion)
        {
            await ViewModel.ChoosePayeeAsync(suggestion);
        }
    }

    // A payee typed in full (not picked from the list) gets the same help when you move on.
    private async void OnPayeeLostFocus(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.HasHabitHint) await ViewModel.ApplyHabitsAsync();
    }

    private void OnUseAmountClick(object sender, RoutedEventArgs e) => ViewModel.UseTypicalAmount();

    private async void OnNoteTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            await ViewModel.UpdateNoteSuggestionsAsync(sender.Text);
        }
    }

    private void OnNoteChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is string note) ViewModel.Memo = note;
    }
}

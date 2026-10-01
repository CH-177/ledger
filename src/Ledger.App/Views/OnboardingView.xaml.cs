using System.ComponentModel;
using Ledger.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Ledger.App.Views;

public sealed partial class OnboardingView : UserControl
{
    public OnboardingViewModel ViewModel { get; }

    public OnboardingView(OnboardingViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += (_, _) => ContinueButton.Focus(FocusState.Programmatic);
    }

    /// <summary>Moves keyboard focus to the main control of each step as it appears.</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(OnboardingViewModel.Step))
        {
            return;
        }
        DispatcherQueue.TryEnqueue(() =>
        {
            switch (ViewModel.Step)
            {
                case OnboardingStep.Welcome: ContinueButton.Focus(FocusState.Programmatic); break;
                case OnboardingStep.CreateAccount: Form.FocusName(); break;
                case OnboardingStep.Ready: OpenButton.Focus(FocusState.Programmatic); break;
            }
        });
    }

    /// <summary>Enter in any field submits the form.</summary>
    private void OnFormKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && ViewModel.CreateAccountCommand.CanExecute(null))
        {
            e.Handled = true;
            ViewModel.CreateAccountCommand.Execute(null);
        }
    }
}

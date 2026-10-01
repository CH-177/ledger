using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Ledger.App.ViewModels;

public enum OnboardingStep
{
    Welcome,
    CreateAccount,
    Ready,
}

/// <summary>The three-screen first launch: welcome, first account, done.</summary>
public sealed partial class OnboardingViewModel : ObservableObject
{
    public OnboardingViewModel(AccountEditorViewModel editor)
    {
        Editor = editor;
        Editor.LoadNew(name: "Checking");
    }

    public AccountEditorViewModel Editor { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWelcome), nameof(IsCreateAccount), nameof(IsReady))]
    public partial OnboardingStep Step { get; set; }

    public bool IsWelcome => Step == OnboardingStep.Welcome;
    public bool IsCreateAccount => Step == OnboardingStep.CreateAccount;
    public bool IsReady => Step == OnboardingStep.Ready;

    /// <summary>Raised when the user leaves onboarding for the main app.</summary>
    public event EventHandler? Completed;

    [RelayCommand]
    private void Continue() => Step = OnboardingStep.CreateAccount;

    [RelayCommand]
    private void Back() => Step = OnboardingStep.Welcome;

    [RelayCommand]
    private async Task CreateAccountAsync()
    {
        if (await Editor.SaveAsync())
        {
            Step = OnboardingStep.Ready;
        }
    }

    /// <summary>True when the user chose to take the welcome tour on leaving setup.</summary>
    public bool TakeTour { get; private set; }

    [RelayCommand]
    private void FinishWithTour()
    {
        TakeTour = true;
        Completed?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Finish() => Completed?.Invoke(this, EventArgs.Empty);
}

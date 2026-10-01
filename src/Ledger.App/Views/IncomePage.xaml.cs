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

public sealed partial class IncomePage : Page
{
    private readonly DialogService _dialogs = App.Services.GetRequiredService<DialogService>();

    public IncomeViewModel ViewModel { get; } = App.Services.GetRequiredService<IncomeViewModel>();

    public IAsyncRelayCommand AddJobCommand { get; }
    public IAsyncRelayCommand PasteCommand { get; }

    public IncomePage()
    {
        AddJobCommand = new AsyncRelayCommand(() => EditJobAsync(null));
        PasteCommand = new AsyncRelayCommand(PasteAsync);
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e) => await ViewModel.LoadAsync();

    // ---- Jobs ---------------------------------------------------------------------------------

    private async void OnEditJobClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is JobRowViewModel row)
        {
            await EditJobAsync(row.Job.Id);
        }
    }

    private async Task EditJobAsync(int? jobId)
    {
        var editor = App.Services.GetRequiredService<JobEditorViewModel>();
        if (jobId is { } id)
        {
            await editor.LoadForEditAsync(id);
        }
        else
        {
            await editor.LoadForCreateAsync();
        }
        if (await _dialogs.ShowAsync(new JobEditorDialog(editor)) == ContentDialogResult.Primary)
        {
            await ViewModel.LoadAsync();
        }
    }

    // ---- Shifts -------------------------------------------------------------------------------

    private async void OnPasteClick(object sender, RoutedEventArgs e) => await PasteAsync();

    private async Task PasteAsync()
    {
        var paste = App.Services.GetRequiredService<PasteScheduleViewModel>();
        await paste.LoadAsync();
        if (await _dialogs.ShowAsync(new PasteScheduleDialog(paste)) == ContentDialogResult.Primary)
        {
            await ViewModel.LoadAsync();
        }
    }

    private async void OnAddShiftClick(object sender, RoutedEventArgs e)
    {
        var editor = App.Services.GetRequiredService<ShiftEditorViewModel>();
        await editor.LoadForCreateAsync();
        await ShowShiftEditorAsync(editor);
    }

    private async void OnEditShiftClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ShiftRowViewModel row)
        {
            var editor = App.Services.GetRequiredService<ShiftEditorViewModel>();
            await editor.LoadForEditAsync(row.Shift);
            await ShowShiftEditorAsync(editor);
        }
    }

    private async Task ShowShiftEditorAsync(ShiftEditorViewModel editor)
    {
        if (await _dialogs.ShowAsync(new ShiftEditorDialog(editor)) == ContentDialogResult.Primary)
        {
            await ViewModel.LoadAsync();
        }
    }

    private async void OnToggleWorkedClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ShiftRowViewModel row)
        {
            await ViewModel.ToggleWorkedAsync(row);
        }
    }

    private async void OnDeleteShiftClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ShiftRowViewModel row
            && await _dialogs.ConfirmAsync($"Delete the shift on {row.DateText}?", "Estimates for that paycheck will update.", "Delete"))
        {
            await ViewModel.DeleteShiftAsync(row);
        }
    }

    // ---- Paychecks ----------------------------------------------------------------------------

    private async void OnPaycheckClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not PaycheckRowViewModel row)
        {
            return;
        }
        switch (await _dialogs.ShowAsync(new PaycheckDialog(row)))
        {
            case ContentDialogResult.Primary:
                await RecordPaycheckAsync(row);
                break;
            case ContentDialogResult.Secondary:
                await EnterPaystubAsync(row);
                break;
        }
    }

    /// <summary>Type in the real paystub and see where Ledger's estimate differs, and why.</summary>
    private async Task EnterPaystubAsync(PaycheckRowViewModel row)
    {
        var paystub = App.Services.GetRequiredService<PaystubViewModel>();
        paystub.Load(row.Paycheck);
        if (await _dialogs.ShowAsync(new PaystubDialog(paystub)) == ContentDialogResult.Primary)
        {
            await ViewModel.LoadAsync();
        }
    }

    /// <summary>
    /// Opens the transaction dialog prefilled with the estimate. The user enters the real amount;
    /// only then is the paycheck linked. Nothing is recorded automatically.
    /// </summary>
    private async Task RecordPaycheckAsync(PaycheckRowViewModel row)
    {
        var p = row.Paycheck;
        var job = ViewModel.Jobs.FirstOrDefault(j => j.Job.Id == p.JobId)?.Job;
        var editor = App.Services.GetRequiredService<TransactionEditorViewModel>();
        try
        {
            // With a paystub entered, start from its real take-home and pay date instead of the estimate.
            await editor.LoadForPaycheckAsync(p.JobName, p.Paystub?.NetCents ?? p.NetCents, p.Paystub?.PayDate ?? p.Period.PayDate,
                job?.DepositAccountId, job?.IncomeCategoryId, fromPaystub: p.Paystub is not null);
        }
        catch (LedgerValidationException)
        {
            await ViewModel.LoadAsync();
            return;
        }

        await _dialogs.ShowAsync(new TransactionEditorDialog(editor));
        if (editor.CreatedTransactionId is { } transactionId)
        {
            await ViewModel.RecordPaycheckAsync(p, transactionId);
        }
    }
}

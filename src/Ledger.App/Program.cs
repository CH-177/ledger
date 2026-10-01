using Ledger.App.Infrastructure;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Ledger.App;

/// <summary>
/// Custom entry point so only one Ledger process ever has a given database open.
/// A second launch hands its activation to the running instance and exits.
/// </summary>
public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        // Keyed by data folder, so the installed app and a dev build don't block each other.
        var key = "Ledger:" + AppPaths.DataDirectory.ToUpperInvariant();
        var primary = AppInstance.FindOrRegisterForKey(key);
        if (!primary.IsCurrent)
        {
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            // Redirect off the STA thread; waiting on it here would deadlock.
            Task.Run(() => primary.RedirectActivationToAsync(activation).AsTask()).Wait();
            return 0;
        }

        Application.Start(_ignored =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App(); // keeps the app alive via Application.Current
        });
        return 0;
    }
}

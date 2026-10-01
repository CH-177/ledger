using Ledger.Core.Services;
using Microsoft.Extensions.Logging;

namespace Ledger.App.Services;

/// <summary>
/// One stop on the welcome tour: what to say, and the page to open and point at in the navigation
/// rail (null for a tip in the middle of the window).
/// </summary>
public sealed record TourStep(string Title, string Text, AppPage? Page, bool IsImportant = false);

/// <summary>
/// The welcome tour: a short walk through the rail, pointing at each page and saying the few things
/// that matter most. It runs once after first setup (or the first launch of a version that has it)
/// and can be replayed from Help or Settings. It only navigates; it never changes data.
/// </summary>
public sealed class WelcomeTour(SettingsService settings, NavigationService navigation, ILogger<WelcomeTour> logger)
{
    /// <summary>"true" once the tour has been finished or dismissed, so it isn't offered again.</summary>
    public const string SeenKey = "Help.TourSeen";

    public IReadOnlyList<TourStep> Steps { get; } =
    [
        new("Welcome to Ledger",
            "Here's a two-minute tour of where things are, and the few things that matter most. You can replay it any time from Help.",
            null),
        new("Overview: where you stand",
            "Available is the real money in your accounts today (credit cards aren't counted). Next two weeks shows bills and paychecks that are coming. Those are forecasts, not money you have yet.",
            AppPage.Overview),
        new("Accounts: start here",
            "Add each account and card with the balance your bank shows today. Ledger works out every balance from that point, so a correct starting balance matters most.",
            AppPage.Accounts),
        new("Transactions: what happened",
            "Add spending and income as it happens (Ctrl+N), or choose Import to bring in a file from your bank. Moving money between your own accounts, like paying a card, is a transfer and never counts as spending.",
            AppPage.Transactions),
        new("Bills: what's coming",
            "Add rent, subscriptions and regular income once. Ledger shows what's due, asks you to record each one when it happens, and forecasts your balance.",
            AppPage.Bills),
        new("Budget: a plan for the month",
            "Set how much to spend in each group. What you've spent fills in from your transactions by itself.",
            AppPage.Budget),
        new("Income: paycheck estimates",
            "Paid by the hour? Add your job and shifts, and Ledger estimates each paycheck with taxes. Estimates never count as money until you record the real paycheck. Taxes are set up for Pennsylvania; if you live elsewhere, change that in Settings → Paycheck taxes.",
            AppPage.Income),
        new("Goals: saving for something",
            "Track savings goals, and sinking funds that set money aside each month for costs you know are coming. Adding money to a goal earmarks it; no money moves.",
            AppPage.Goals),
        new("What if?: try before you buy",
            "Ask \"Can I afford this?\" or try a change, like working fewer hours, and see what happens to your balance. Nothing here is ever saved as real data.",
            AppPage.Plan),
        new("Back up your data",
            "Your data lives only on this PC. Every so often, open Settings → Data → Back up now… and save the file to a USB drive or a cloud folder. If this PC ever fails, that file is how you get everything back.",
            AppPage.Settings, IsImportant: true),
        new("Help is always here",
            "Press F1 on any page for help with it, or open Help to search everything. That's also where you can replay this tour.",
            AppPage.Help),
    ];

    public bool IsRunning { get; private set; }

    public int Index { get; private set; }

    public TourStep Current => Steps[Index];

    public bool IsFirst => Index == 0;

    public bool IsLast => Index == Steps.Count - 1;

    /// <summary>"3 of 10", for the tip's footer; the welcome step isn't counted.</summary>
    public string ProgressText => IsFirst ? "" : $"{Index} of {Steps.Count - 1}";

    /// <summary>Raised when the tour starts, moves, or ends.</summary>
    public event EventHandler? Changed;

    public async Task<bool> HasBeenSeenAsync()
    {
        try
        {
            return await settings.GetAsync(SeenKey) == "true";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Couldn't read whether the tour was seen");
            return true; // never nag because of a read error
        }
    }

    /// <summary>Starts from the beginning, or straight at the first page when the user just chose to take it.</summary>
    public void Start(bool skipWelcome = false)
    {
        IsRunning = true;
        Index = skipWelcome ? 1 : 0;
        Show();
    }

    public void Next()
    {
        if (!IsRunning)
        {
            return;
        }
        if (IsLast)
        {
            End();
            return;
        }
        Index++;
        Show();
    }

    public void Back()
    {
        if (IsRunning && Index > 0)
        {
            Index--;
            Show();
        }
    }

    /// <summary>Finishes or dismisses the tour, and remembers not to offer it again.</summary>
    public void End()
    {
        if (!IsRunning)
        {
            return;
        }
        IsRunning = false;
        Changed?.Invoke(this, EventArgs.Empty);
        _ = MarkSeenAsync();
    }

    private void Show()
    {
        if (Current.Page is { } page)
        {
            navigation.Navigate(page);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Remembers not to offer the tour again (also when it was declined during setup).</summary>
    public async Task MarkSeenAsync()
    {
        try
        {
            await settings.SetAsync(SeenKey, "true");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Couldn't remember that the tour was seen");
        }
    }
}

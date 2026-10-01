using Ledger.App.Services;

namespace Ledger.App.Help;

/// <summary>A paragraph, a bullet, or a small heading inside a help topic.</summary>
public enum HelpBlockKind
{
    Paragraph,
    Bullet,
    Heading,
    /// <summary>Something to be careful about, shown with a warning mark.</summary>
    Important,
}

public sealed record HelpBlock(HelpBlockKind Kind, string Text);

/// <summary>One help topic: what something is, what it does, and how to use it.</summary>
public sealed record HelpTopic(
    string Id,
    string Group,
    string Title,
    string Summary,
    string Glyph,
    IReadOnlyList<HelpBlock> Blocks,
    AppPage? Page = null,
    IReadOnlyList<string>? Related = null,
    string Keywords = "")
{
    /// <summary>
    /// How well the topic answers <paramref name="query"/>: 0 unless every word appears somewhere in it,
    /// and higher for words in the title, then the keywords and summary, then the text.
    /// </summary>
    public int Score(string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return 1;
        }
        var body = string.Join(' ', Blocks.Select(b => b.Text));
        var total = 0;
        foreach (var word in words)
        {
            bool In(string text) => text.Contains(word, StringComparison.CurrentCultureIgnoreCase);
            var score = (In(Title) ? 8 : 0) + (In(Keywords) ? 4 : 0) + (In(Summary) ? 3 : 0) + (In(body) ? 1 : 0);
            if (score == 0)
            {
                return 0;
            }
            total += score;
        }
        return total;
    }
}

/// <summary>
/// Everything in Help, in reading order. Written in the app's own words: the labels here are the
/// labels on screen, so keep them in step when a page changes.
/// </summary>
public static class HelpContent
{
    public const string GettingStarted = "Getting started";
    public const string Pages = "Pages";
    public const string Ideas = "How the numbers work";
    public const string YourData = "Your data";
    public const string Troubleshooting = "If something looks wrong";

    private static HelpBlock P(string text) => new(HelpBlockKind.Paragraph, text);
    private static HelpBlock B(string text) => new(HelpBlockKind.Bullet, text);
    private static HelpBlock H(string text) => new(HelpBlockKind.Heading, text);
    private static HelpBlock Warn(string text) => new(HelpBlockKind.Important, text);

    public static IReadOnlyList<HelpTopic> Topics { get; } =
    [
        // ---- Getting started ------------------------------------------------------------------

        new("welcome", GettingStarted, "Ledger in two minutes", "What Ledger is for and the few things worth knowing first.", "\uE80F",
        [
            P("Ledger keeps track of your money on this PC: what's in your accounts, where it goes, what's coming up, and what you're saving for. There's no account to create and nothing is ever sent anywhere."),
            H("The basic routine"),
            B("Add each bank account and card once, with the balance your bank shows today (Accounts)."),
            B("Record what you spend and earn as it happens, or import a file from your bank every week or two (Transactions)."),
            B("Add the bills and income that repeat, so Ledger can show what's coming (Bills)."),
            B("Plan each month's spending (Budget) and what you're saving for (Goals)."),
            B("Glance at Overview to see where you stand."),
            Warn("Your data exists only on this PC. Save a backup somewhere else now and then (Settings → Data → Back up now…)."),
            P("You can replay the welcome tour at any time from the top of this Help page."),
        ], Related: ["first-steps", "backups", "shortcuts"], Keywords: "start tour introduction basics"),

        new("first-steps", GettingStarted, "Setting up", "Accounts, starting balances, categories, bills and jobs: the order that works best.", "\uE710",
        [
            H("1. Accounts"),
            P("Add every account you want Ledger to follow: checking, savings, cash, credit cards. For each, enter the balance your bank shows today. Ledger works out every balance from that point by adding and subtracting what you record."),
            B("The balance date says which day that balance is from (today, unless you change it). Transactions dated on or before it are treated as already included, so they never count twice. If today's purchases aren't in the bank's balance yet, set the balance date to yesterday and enter them."),
            B("For a credit card, enter the amount you owe, as a positive number."),
            H("2. Categories"),
            P("Ledger starts with common spending, income and savings categories. Rename, add or archive them in Settings → Manage categories. A subcategory's spending counts toward its group in the budget."),
            H("3. Bills and income"),
            P("On Bills, add rent, phone, subscriptions and other things that repeat, and any regular income that isn't a paycheck from shifts. They drive the forecast."),
            H("4. Jobs (optional)"),
            P("If you're paid by the hour, add your job on Income and enter your shifts. Ledger estimates each paycheck, with taxes, so you know what's coming before payday."),
            H("5. A backup"),
            P("Once you've set things up, save a backup to a USB drive or cloud folder (Settings → Data)."),
        ], Page: AppPage.Accounts, Related: ["accounts", "balances", "categories", "backups"], Keywords: "setup begin new starting balance"),

        new("shortcuts", GettingStarted, "Keyboard shortcuts", "Faster ways to get around.", "\uE765",
        [
            B("F1: open Help for the page you're on."),
            B("Ctrl+N on Transactions: add a transaction."),
            B("Ctrl+F on Transactions: search."),
            B("Enter in a budget amount: save that line."),
            B("Enter when creating your first account: create it."),
            B("Tab and Shift+Tab move between fields; Esc closes a dialog without saving."),
        ], Keywords: "keys keyboard hotkeys f1 ctrl"),

        // ---- Pages ----------------------------------------------------------------------------

        new("overview", Pages, "Overview", "Where you stand today, this month so far, and the next two weeks.", "\uE80F",
        [
            B("Available: what's in all your open accounts today, not counting credit cards. Only real, recorded money; nothing expected."),
            B("Free to spend (if you have sinking funds): Available minus what your funds are holding."),
            B("This month: income, spending and saving from the 1st through today, and what remains."),
            B("Next two weeks: bills, paychecks and other expected money, with the balance after each. These are forecasts, marked as such."),
            B("Budget and Goals: a short summary; open the page for details."),
        ], Page: AppPage.Overview, Related: ["available", "forecast", "sinking-funds"], Keywords: "dashboard home summary"),

        new("transactions", Pages, "Transactions", "Every payment, deposit and transfer, with search and filters.", "\uE8FD",
        [
            P("Add transaction (or Ctrl+N) records spending, income, a transfer, or a refund. Pick the account, the amount (always positive), the date and a category."),
            B("Spending and income count in your budget and reports."),
            B("Transfer: money moving between two of your accounts, including paying a credit card. Never counts as spending or income. Choose \"Someone else's account\" for money to or from a family member's account."),
            B("Refund: money back for something you bought. It lowers spending in that category instead of counting as income."),
            B("Pending: not yet cleared by your bank. It still counts in your balance."),
            P("As you type a payee, Ledger suggests the category, account and amount you usually use there. Nothing is saved until you press Save."),
            P("Search finds payees, notes, categories and amounts. After deleting, Undo appears right away to bring it back."),
            P("Import brings in a file downloaded from your bank. See \"Importing from your bank\"."),
        ], Page: AppPage.Transactions, Related: ["import", "transfers", "refunds", "suggestions"], Keywords: "add edit delete search filter payee pending cleared"),

        new("import", Pages, "Importing from your bank", "Bring in a CSV or QFX file instead of typing everything.", "\uE8B5",
        [
            P("On your bank's website, open an account's transactions and look for Download or Export. Choose QFX, OFX or Quicken if offered (they're the most reliable), otherwise CSV. Then in Ledger: Transactions → Import → Choose a file…"),
            H("Reviewing"),
            B("Every row is shown with a suggested kind, payee and category. Change anything before importing."),
            B("\"Already imported\" rows are skipped. \"Probably already in Ledger\" rows (something you typed in yourself) start unticked; tick them only if they're really new."),
            B("Rows that pay a bill or match a predicted paycheck say so, and count as that bill or paycheck being recorded."),
            B("Tick Remember on a row to use the same payee, category or transfer account for that description next time."),
            H("Saving"),
            P("Nothing is saved until you press Import. The import can be undone as a whole from Recent imports."),
            P("If the bank shows a balance in the file, \"Use the bank's balance\" sets the account to match the bank exactly as of that day."),
            Warn("Import into the account the file came from. Each account's file layout is remembered separately."),
        ], Page: AppPage.Import, Related: ["transactions", "balance-off", "duplicates"], Keywords: "csv qfx ofx download bank file duplicate"),

        new("budget", Pages, "Budget", "A monthly plan per spending group, compared with what you've actually spent.", "\uE9D5",
        [
            P("Enter a plan for each spending group. Spent comes from your transactions, so it's always up to date; changing the plan never changes what was spent."),
            B("Spending in a subcategory counts toward its group. Uncategorised spending is shown separately so it doesn't hide."),
            B("Each group says when it's nearly used (90% of its plan) and when it's over."),
            B("Copy last month's plan fills in a new month from the one before."),
            B("Refunds lower the month they arrive in."),
            H("Sinking funds"),
            P("Your sinking funds are listed beside the plan, with \"Set aside\" for this month's amount. Setting money aside isn't spending, so it isn't part of the plan."),
        ], Page: AppPage.Budget, Related: ["sinking-funds", "categories", "refunds"], Keywords: "plan monthly spending limit categories"),

        new("income", Pages, "Income and paychecks", "Paycheck estimates from your hourly shifts, with taxes.", "\uE821",
        [
            P("Add your job (rate, how often you're paid, your W-4 choices and where you work), then add shifts or paste your schedule. Ledger works out each paycheck: hours, breaks, overtime over 40 hours a week, and taxes."),
            B("Estimated take-home pay is a forecast, never money in your account. It feeds the forecast on Overview and Bills."),
            B("When you're paid, open the paycheck and choose \"Paid? Record the real amount\" (or import the deposit). The estimate stays beside it for comparison."),
            B("Enter paystub… compares a real paystub line by line with Ledger's math and points at what differs. It can also confirm your real payday."),
            B("\"Payday not confirmed\" means Ledger is assuming your payday until your first paystub confirms it."),
            P("Taxes: see \"Paycheck estimates and taxes\" for what's included and how to set your local tax."),
        ], Page: AppPage.Income, Related: ["paycheck-taxes", "paycheck-off", "forecast"], Keywords: "job shift hours wage pay paystub w-4 overtime schedule"),

        new("bills", Pages, "Bills and recurring", "Things that repeat, what's coming, and what needs recording.", "\uE787",
        [
            P("Add recurring bills, subscriptions, income and transfers with how often they happen. Ledger works out every date from the first one; it never creates anything on its own unless you tick \"Record automatically on its due date\"."),
            B("Needs recording: a date that has passed. Record creates the real transaction (change the amount if it differed); Skip removes that date."),
            B("Coming up: the next dates, with the forecast balance after each."),
            B("Looks like it repeats: payments Ledger noticed happening on a schedule. Add one as a bill with a click, or choose Don't suggest."),
            B("A bill with recorded payments can be paused but not deleted, so its history stays."),
            P("Estimated amounts (like a utility bill that varies) are never recorded automatically."),
        ], Page: AppPage.Bills, Related: ["forecast", "suggestions"], Keywords: "recurring subscription rent due skip record automatic schedule forecast"),

        new("goals", Pages, "Goals", "Savings goals with progress, and sinking funds for known costs.", "\uE7C1",
        [
            P("A goal is something you're saving toward. Track it by hand (Add money / Take money out) or let it follow a savings account's balance."),
            B("Adding money to a goal is an earmark: it doesn't move money between accounts."),
            B("With a target date, Ledger suggests how much to put in each month."),
            B("History shows every amount added or taken out."),
            P("Tick \"This is a sinking fund\" for money you set aside every month for a cost you know is coming. See \"Sinking funds\"."),
        ], Page: AppPage.Goals, Related: ["sinking-funds"], Keywords: "save saving target progress"),

        new("plan", Pages, "What if?", "Try changes against your forecast without changing anything.", "\uEA80",
        [
            P("Nothing on this page is real. It's a sandbox on top of your forecast, so try anything."),
            H("Can I afford this?"),
            P("Enter an amount, a date and the account. Ledger answers in plain words, looking 60 days past the purchase, and tells you your lowest point. Add a cushion to keep if you never want to go below a certain amount."),
            H("Scenarios"),
            P("Add changes: a purchase, extra money in, working fewer or more hours, a new pay rate, a new bill or income, or stopping a bill. Compare shows things as they are next to things with your changes: your lowest balance, the ending balance, and when you'd drop below $0."),
            B("Save a scenario with a name to come back to it."),
            B("Make it real… opens the normal editor filled in. Only pressing Save there creates anything."),
        ], Page: AppPage.Plan, Related: ["forecast"], Keywords: "scenario afford purchase plan simulate hours raise"),

        new("accounts", Pages, "Accounts", "Your bank accounts, cash and cards, and net worth.", "\uE8C7",
        [
            P("Each account's balance is its starting balance plus everything recorded since. Net worth is what you own minus what you owe on cards and loans, for accounts marked \"Include in net worth\"."),
            B("The ⋯ menu on an account: Edit, View transactions, Reconcile with a statement…, Archive."),
            B("An account with history can't be deleted, only archived. Archived accounts keep their history and can be restored."),
            B("Changing a starting balance moves the whole balance but never rewrites history."),
        ], Page: AppPage.Accounts, Related: ["balances", "reconcile", "balance-off"], Keywords: "bank checking savings credit card cash net worth archive"),

        new("reconcile", Pages, "Reconciling with a statement", "Make sure Ledger and your bank statement agree.", "\uE9D5",
        [
            P("When a statement arrives, open Accounts → ⋯ → Reconcile with a statement…, enter the statement's end date and ending balance, then tick each transaction that appears on the statement."),
            B("The difference must reach exactly $0.00 before Finish. Finish marks those transactions Reconciled."),
            B("If it won't reach $0, \"Where the difference might be\" suggests likely causes: a missing or extra transaction, or two digits swapped."),
            B("Reconciled transactions are protected: changing their amount or date asks twice."),
            B("Undo takes back the most recent reconciliation."),
        ], Page: AppPage.Accounts, Related: ["balance-off", "accounts"], Keywords: "statement match bank reconciled difference"),

        new("reports", Pages, "Reports", "Spending by category, cash flow and net worth over time.", "\uE9D2",
        [
            B("Spending by category: where the month's money went (the same numbers as Budget)."),
            B("Cash flow: income and spending month by month. Transfers are left out."),
            B("Net worth: at the end of each month, and today."),
        ], Page: AppPage.Reports, Keywords: "chart history trend"),

        new("settings", Pages, "Settings", "Appearance, categories, suggestions, paycheck taxes and your data.", "\uE713",
        [
            B("Appearance: light or dark (or follow Windows), four colour schemes, and the decorative art beside pages on wide windows."),
            B("Categories: Manage categories to add, rename, group or archive."),
            B("Suggestions: turn off spotting repeating payments."),
            B("Paycheck taxes: where you live, for paycheck estimates."),
            B("Data: backups, restore and export. See \"Backups and restore\"."),
            B("About: the version you're running, and the welcome tour."),
        ], Page: AppPage.Settings, Related: ["backups", "paycheck-taxes", "categories"], Keywords: "theme dark light colour options preferences"),

        // ---- How the numbers work -------------------------------------------------------------

        new("available", Ideas, "Available and Free to spend", "The two numbers at the top of Overview.", "\uE8C7",
        [
            P("Available is the money in all your open accounts today, except credit cards: real, recorded money only. Bills that haven't happened yet and estimated paychecks aren't in it."),
            P("Free to spend is Available minus what your sinking funds are holding: money you haven't already promised to something. It only appears once you have a sinking fund."),
            P("For what's coming next, look at Next two weeks on Overview or the forecast on Bills."),
        ], Page: AppPage.Overview, Related: ["forecast", "sinking-funds"], Keywords: "available free spend balance money"),

        new("balances", Ideas, "How balances work", "Starting balances, and why older bank rows don't double-count.", "\uE8EF",
        [
            P("Ledger never stores a balance. An account's balance is its starting balance plus every transaction dated on or before today. Future-dated ones join on their date."),
            P("A starting balance is the bank's balance at the end of a particular day. Transactions on or before that day are already inside it: they count in your spending history, but don't move the balance again. That's why importing last month's bank file never throws your balance off."),
            P("If your balance and the bank's disagree, see \"My balance doesn't match the bank\"."),
        ], Page: AppPage.Accounts, Related: ["balance-off", "accounts"], Keywords: "starting balance date double counted"),

        new("transfers", Ideas, "Transfers", "Moving money between accounts, paying a card, and money to or from family.", "\uE8AB",
        [
            P("A transfer moves money from one of your accounts to another. It's never spending or income, so your budget isn't affected."),
            B("Paying a credit card is a transfer from checking to the card. The purchases on the card were already counted as spending when you made them."),
            B("Moving money into savings counts as \"Saved\" on Overview."),
            B("Someone else's account (like a parent's or partner's): choose Transfer, then \"Someone else's account (outside)\". The money really arrives or leaves, but it isn't treated as income or spending."),
        ], Related: ["transactions", "refunds"], Keywords: "transfer move credit card payment savings family outside"),

        new("refunds", Ideas, "Refunds", "Money back from a store lowers your spending instead of counting as income.", "\uE7A7",
        [
            P("Record money back for something you bought as a Refund, in the same category as the purchase. It adds to your account and lowers that category's spending in the month the refund arrives."),
            P("Importing suggests a refund when the description says so, or when the money comes from a store you've spent at."),
        ], Related: ["transactions", "budget"], Keywords: "return refund money back store"),

        new("forecast", Ideas, "The forecast", "How Ledger looks ahead, and why it's never money you have.", "\uE9D9",
        [
            P("The forecast starts from today's real balance and adds what's expected: bills and income from Bills that aren't recorded yet, transactions you entered with a future date, and estimated paychecks from Income."),
            B("Everything after today is a forecast and is labelled that way."),
            B("Dates that passed without being recorded are placed on today until you record or skip them."),
            B("On the same day, money out is counted before money in, so the lowest point is never understated."),
            B("Credit cards aren't included; paying one shows up when you add the payment as a recurring transfer."),
        ], Page: AppPage.Bills, Related: ["bills", "plan", "income"], Keywords: "predict future expected projection lowest"),

        new("paycheck-taxes", Ideas, "Paycheck estimates and taxes", "What's included, and setting your local tax.", "\uE8EF",
        [
            P("Estimates follow the IRS withholding tables for the year (from your W-4 choices on the job), plus Social Security and Medicare."),
            H("Living and working in Pennsylvania"),
            P("Ledger also includes PA income tax, PA unemployment, the local earned income tax and the local services tax. Set where you live in Settings → Paycheck taxes, and where you work on the job. Your municipality's PSD code and rates are on the statewide list at munstats.pa.gov."),
            H("Living or working elsewhere"),
            P("Turn off \"I live and work in Pennsylvania\" in Settings → Paycheck taxes. Estimates then include federal tax, Social Security and Medicare only, so your real paychecks may be a little lower if your state has an income tax."),
            Warn("Each year Ledger needs that year's tax tables. Until an update adds them, it uses the latest year it has and says so on each paycheck."),
        ], Page: AppPage.Settings, Related: ["income", "paycheck-off"], Keywords: "tax withholding federal state local eit lst psd w-4 pennsylvania"),

        new("sinking-funds", Ideas, "Sinking funds", "Set money aside each month for costs you know are coming.", "\uE7B8",
        [
            P("A sinking fund is money set aside for something you know is coming: car repairs, gifts, a yearly insurance bill. Create one on Goals and tick \"This is a sinking fund\", with a monthly amount and the category it pays for."),
            B("Each month, Budget offers \"Set aside\" for each fund. Setting aside is an earmark; no money moves."),
            B("When you add spending in that category, the editor offers \"Pay from a sinking fund\". It still counts as spending; the fund's balance goes down."),
            B("A fund can't go below zero."),
            B("Overview shows Free to spend: Available minus what your funds hold."),
        ], Page: AppPage.Goals, Related: ["goals", "available", "budget"], Keywords: "sinking fund set aside envelope save monthly"),

        new("suggestions", Ideas, "Suggestions", "How Ledger learns from your own entries.", "\uE945",
        [
            P("Ledger notices patterns in your own transactions, on this PC only. It never saves anything by itself."),
            B("Payees: the category, account and usual amount you use for a payee, once you've used it at least twice."),
            B("Notes: notes you've written before, as you type."),
            B("Repeats: payments that happen weekly, every two weeks or monthly show on Bills as \"Looks like it repeats\". Turn this off in Settings → Suggestions."),
        ], Related: ["transactions", "bills"], Keywords: "autofill autocomplete learn suggest repeat"),

        new("categories", Ideas, "Categories", "Groups and subcategories for spending, income and savings.", "\uE8FD",
        [
            P("Categories are in two levels at most: a group (like Food) and its subcategories (Groceries, Eating out). Budgets are planned per group."),
            B("Manage them in Settings → Manage categories."),
            B("Categories are archived rather than deleted, so past transactions keep them."),
            B("Spending categories are used for spending and refunds, income ones for income, and savings ones for transfers."),
        ], Page: AppPage.Categories, Related: ["budget"], Keywords: "category group subcategory archive rename"),

        // ---- Your data ------------------------------------------------------------------------

        new("privacy", YourData, "Where your data lives", "On this PC, in one file, and nowhere else.", "\uE72E",
        [
            P("Everything is stored in your Windows user folder (Settings → Data shows exactly where). Ledger has no account, no cloud and no tracking, and never connects to the internet."),
            B("Other people who use this PC with their own Windows account have their own separate Ledger data."),
            B("Uninstalling Ledger leaves your data in place, so reinstalling picks up where you left off."),
            B("The portable copy (from the portable zip) keeps its data in a Data folder beside Ledger.exe instead, so it can travel on a USB stick. Keep the folder together."),
        ], Page: AppPage.Settings, Related: ["backups", "new-pc"], Keywords: "privacy local offline cloud internet folder portable usb"),

        new("backups", YourData, "Backups and restore", "The one thing that protects your data if this PC fails.", "\uE8F1",
        [
            Warn("Your data exists only on this PC. If the drive fails or the PC is lost, a backup saved somewhere else is the only way to get it back."),
            B("Back up now… saves your data as one file. Put it on a USB drive or in a cloud folder (OneDrive, Google Drive)."),
            B("Automatic daily backups are on by default and keep the last 10, but they're on this PC, so they protect against mistakes, not a dead drive."),
            B("Restore a backup… replaces your current data with the backup. Your current data is saved first, just in case."),
            B("Ledger also saves a copy before every update that changes how data is stored."),
        ], Page: AppPage.Settings, Related: ["new-pc", "export"], Keywords: "backup restore copy save usb onedrive lost"),

        new("export", YourData, "Exporting", "Take your data to a spreadsheet or another app.", "\uE78C",
        [
            B("Export transactions (CSV): opens in Excel or Google Sheets."),
            B("Export everything (JSON): every account, transaction, budget, bill and goal, in a documented format other apps can read."),
            P("An export is a copy for other apps; to keep your data safe, use a backup instead."),
        ], Page: AppPage.Settings, Related: ["backups"], Keywords: "export csv json excel spreadsheet"),

        new("new-pc", YourData, "Moving to a new PC", "Bring everything across with one backup file.", "\uE7F8",
        [
            B("On the old PC: Settings → Data → Back up now…, saved to a USB drive or cloud folder."),
            B("On the new PC: install Ledger and open it."),
            B("On the first screen, create any account (it'll be replaced), then Settings → Data → Restore a backup… and choose the file."),
        ], Page: AppPage.Settings, Related: ["backups"], Keywords: "new computer transfer move migrate"),

        new("updating", YourData, "Updating and uninstalling", "New versions keep your data.", "\uE895",
        [
            B("To update, close Ledger and run the new version's Setup. Your data is kept, and backed up first if the update changes how it's stored."),
            B("To uninstall, use Windows Settings → Apps → Installed apps → Ledger. Your data stays in place unless you delete its folder yourself."),
            B("The portable copy: replace everything in its folder except the Data folder."),
            B("The version you have is shown in Settings → About."),
        ], Related: ["privacy", "backups"], Keywords: "update upgrade install uninstall remove version"),

        // ---- Troubleshooting ------------------------------------------------------------------

        new("balance-off", Troubleshooting, "My balance doesn't match the bank", "The usual causes, from most to least likely.", "\uE7BA",
        [
            B("Pending purchases: your bank's \"available\" balance subtracts holds that haven't posted yet. Compare with the posted (current) balance, or record the pending item too."),
            B("A missing or doubled transaction: Accounts → ⋯ → View transactions and compare the last few weeks with the bank."),
            B("The starting balance: if it was entered from a different day, edit the account and correct it."),
            B("Importing: the import screen can set the account straight to the bank's balance with \"Use the bank's balance\"."),
            B("For a thorough check, reconcile with your latest statement: it shows exactly which transactions differ."),
        ], Page: AppPage.Accounts, Related: ["reconcile", "balances", "import"], Keywords: "wrong balance mismatch different bank pending"),

        new("duplicates", Troubleshooting, "Import says rows are already in Ledger", "Why some rows start unticked.", "\uE8C8",
        [
            B("\"Already imported\": that exact bank transaction came in with an earlier file. It's skipped."),
            B("\"Probably already in Ledger\": something with the same amount, close in date, is already recorded, usually one you typed in yourself. Leave it unticked unless it's really a second transaction."),
            B("After importing checking, the savings side of the same transfers shows as already in Ledger. That's expected."),
        ], Page: AppPage.Import, Related: ["import"], Keywords: "duplicate twice already imported"),

        new("paycheck-off", Troubleshooting, "My paycheck was different from the estimate", "Find out why with your paystub.", "\uE8EF",
        [
            B("Hours: the most common reason. Check your shifts match the hours on the paystub, including training and breaks."),
            B("Open the paycheck on Income and choose Enter paystub…. Ledger compares each line and says which one differs and the likely reason (W-4 choices, a benefit deduction, a different local tax rate)."),
            B("Check Settings → Paycheck taxes and the job's local tax settings."),
            B("The first paycheck of a job is often different: training hours, a partial week, or a different payday."),
        ], Page: AppPage.Income, Related: ["income", "paycheck-taxes"], Keywords: "paycheck wrong estimate different taxes paystub"),

        new("cant-delete", Troubleshooting, "I can't delete something", "Why Ledger archives instead of deleting.", "\uE74D",
        [
            P("Accounts and categories with history, and bills with recorded payments, can't be deleted, because that would change your past numbers. Archive or pause them instead: they disappear from everyday lists but your history stays correct."),
            P("Transactions can be deleted; Undo brings one back right away."),
        ], Related: ["accounts", "categories", "bills"], Keywords: "delete remove archive pause"),

        new("wont-open", Troubleshooting, "Ledger couldn't open your data", "What to try.", "\uE783",
        [
            B("Make sure Ledger isn't already open (check the taskbar), then choose Try Again."),
            B("Restart the PC and open Ledger again."),
            B("If it still won't open, your data folder holds Backups (automatic copies) and Logs (technical details that never leave this PC). A backup can be restored from Settings → Data once Ledger opens."),
        ], Keywords: "error crash open fail broken"),
    ];

    public static HelpTopic? Find(string id) => Topics.FirstOrDefault(t => t.Id == id);

    /// <summary>The topic that explains <paramref name="page"/>, for F1.</summary>
    public static string TopicFor(AppPage? page) => page switch
    {
        AppPage.Overview => "overview",
        AppPage.Transactions => "transactions",
        AppPage.Import => "import",
        AppPage.Budget => "budget",
        AppPage.Income => "income",
        AppPage.Bills => "bills",
        AppPage.Goals => "goals",
        AppPage.Plan => "plan",
        AppPage.Accounts => "accounts",
        AppPage.Reconcile => "reconcile",
        AppPage.Reports => "reports",
        AppPage.Settings => "settings",
        AppPage.Categories => "categories",
        _ => "welcome",
    };
}

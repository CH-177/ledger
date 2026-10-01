# Changelog

Versions are `MAJOR.MINOR.PATCH`. Each phase or feature bumps MINOR; small fixes bump PATCH.
The version lives in `Directory.Build.props` and is shown in Settings → About.

## 3.0.3

First public release on GitHub.

- **Example data made generic.** Tests, docs and code comments used real-world details from
  development (an employer, towns, a bank and its file formats, store names). They're replaced with
  made-up ones (Riverside Market, Maple Boro, Riverton City, Example CU…). Nothing in the app
  changes: the same 490 tests pass.
- The 3.0.0 `HomeTaxDefault` upgrade step no longer writes anything; the only data it was for has
  already been upgraded.
- The public history starts here. Earlier history is kept privately.
- `INSTALL.md`: step-by-step install instructions for people downloading from GitHub (added after
  the release; the app is unchanged).

## 3.0.2

- **Faster `install.ps1`.** It used to rebuild Ledger from source every time (30+ seconds, mostly the
  WinUI page compiler and trimming), even when nothing had changed. Now the build in
  `artifacts\publish` is stamped with a fingerprint of the source files and reused while they match,
  so installing a version that's already been built just copies files (a second or two, like the
  Setup installer). Any change to the source still triggers a fresh build; `-Rebuild` forces one.
- `package.ps1` builds into the same place, so installing right after packaging is instant too.

## 3.0.1

- **Side art.** On wide windows, the empty space to the right of each page now has soft abstract
  shapes and squiggles, drawn in your colour scheme's own colours (so it changes with the scheme and
  light/dark). It sits behind everything, can't be clicked, is skipped by screen readers, hides when
  the window is too narrow or Windows high contrast is on, and can be turned off in Settings →
  Appearance.

## 3.0.0

Ready to share: a welcome tour, built-in Help, and a Windows installer you can give to other people.

- **Welcome tour.** A two-minute walk through the sidebar that points at each page and says the few
  things that matter most (starting balances, what's real versus forecast, and backing up). It's
  offered after first setup, and once to anyone upgrading. Replay it from Help or Settings → About.
- **Help** (the ? in the sidebar). 34 topics in five groups: getting started, every page, how the
  numbers work, your data, and what to do when something looks wrong. Search ranks the best match
  first. **F1** on any page opens its topic.
- **Paycheck taxes outside Pennsylvania.** Settings → Paycheck taxes has "I live and work in
  Pennsylvania". Turned off, estimates include federal tax, Social Security and Medicare only.
- **Neutral defaults for new users.** A fresh install no longer assumes a home location for local tax
  (it used to default to one particular town), and the examples on screen no longer name a real
  employer or bank. Existing data keeps its setting: the upgrade writes the old default in for anyone
  who never saved one.
- **Portable mode.** A `Ledger.portable` file beside Ledger.exe keeps the data in a `Data` folder next
  to it, so Ledger can run from a USB stick.
- **Windows installer** (`scripts/package.ps1` → `dist/Windows-v3.0.0/`):
  - `Ledger-Setup-3.0.0.exe` (38 MB): installs for the current user with no administrator rights,
    adds desktop and Start menu shortcuts and an entry in Installed apps, updates in place, and
    comes with an uninstaller. It never touches Ledger's data.
  - `Ledger-3.0.0-portable.zip`: unzip and run.
  - `README.txt` for whoever receives it.
- **Polish and tests:** the smoke test covers the tour, Help, What if?, Import and Reconcile; the
  first-account screen offers the tour or "Skip the tour"; the upgrade was checked on a copy of real
  2.4.2 data (every record kept, integrity ok). 490 tests.

## 2.9.9

Sinking funds and scenario planning, together. This completes V2.

- **Sinking funds.** Set money aside for costs you know are coming (car repairs, gifts, a yearly bill).
  - In Goals, add a goal and tick "This is a sinking fund". Give it a monthly amount and the spending
    category it pays for.
  - **Budget** lists your funds with a "Set aside $X" button for the month. Setting aside never moves
    money between accounts.
  - **Paying from a fund:** when you add spending in a fund's category, the editor offers "Pay from a
    sinking fund" already set to that fund (change it if you like). It still counts as spending, and a
    fund can't go below zero.
  - **Overview** shows, under Available: "Set aside in funds −$X → Free to spend $Y".
- **What if?** (new in the sidebar)
  - **Can I afford this?** Enter an amount, date and account: "Yes. With it, your lowest point would be
    $312 on Dec 12", or "Not without going below $0…" with when and how low. Set a cushion to keep, too.
  - **Try a scenario:** add changes (a purchase, extra money in, working a % of your scheduled hours or
    a set number of hours a week, a new pay rate, a new bill or income, or stopping one) and compare with
    things as they are: lowest point, ending balance, and when it would drop below $0 or your cushion.
    Hours and pay changes are re-priced with taxes and overtime, like real paychecks. Goals that follow
    an account show what that account would hold on their target date.
  - Save scenarios by name and reopen them. Nothing in a scenario is ever real. "Make it real…" opens
    the usual editor already filled in, and only saving there creates anything.
- The JSON export includes sinking-fund fields, `paidFromGoalId` on transactions, and saved scenarios
  (as a setting).
- 487 tests.

## 2.6.0

- **Reconcile with a statement.** On Accounts, open an account's ⋯ menu and choose "Reconcile with a
  statement…".
  - Enter the statement's end date and ending balance (for a card, the amount owed), then tick each
    transaction that's on it. Cleared ones are ticked for you.
  - The difference updates as you tick. At $0.00, **Finish** marks those transactions reconciled, and
    the next statement starts from there.
  - When it's off, Ledger points at likely causes: a transaction that would fix it exactly, one dated
    just after the statement, one still pending, or an amount with two digits swapped.
  - Past statements are listed, and the latest can be undone.
- **Reconciled transactions are protected:** changing one's amount, date, kind or account needs a second
  Save, and deleting one asks first, because either would break a statement that matched.
- The JSON export includes reconciliations.
- 481 tests.

## 2.5.0

- **Smart suggestions (autofill).** Ledger learns from your own entries, on this PC only. It only
  suggests: you see everything it fills in, and nothing is saved until you save.
  - **Payees:** the list shows the payees you use most first. Pick one (or type one you've used
    before) and Ledger fills in the category you usually use there and, for a new entry, the
    account you usually pay from. It also shows "Usually $11.99 from Checking" with a button to use
    that amount. It learns from your latest 12 entries, so a changed habit wins; a payee used only
    once teaches nothing.
  - **Notes:** start typing a note and pick from notes you've written before, for any payee (like
    "gas" or "lunch w/ Sam").
  - **"Looks like it repeats":** on Bills, entries you've made on a regular schedule (monthly around
    the same day, weekly, or every two weeks, at least 3 times in a row, amounts within 20%) are
    offered as bills. "Make it a bill" opens the bill form already filled in, starting on the next
    expected date; "Don't suggest" hides it for good. You can turn this off in Settings →
    Suggestions.
- **Imported paychecks count as recorded.** A deposit into your pay account near a payday, from your
  employer (or close to Ledger's estimate), shows "Counts as your Riverside Market paycheck for Oct 2 (Ledger
  estimated $46.40)". Import it and the paycheck is recorded exactly as if you'd used "Record this
  paycheck", so the estimate stops counting in the forecast. Untick the box to keep them separate;
  undoing the import makes it an estimate again.
- 475 tests.

## 2.4.2

- **Fix: importing past transactions no longer throws your balance off.** The starting balance you
  typed in was your bank's balance that day, so transactions from before then were already in it;
  importing them counted them a second time.
  - Every account now has a **balance date**: the day its starting balance is from (new accounts:
    the day you add them; change it in Edit account). Transactions on or before it still count as
    spending and income, but no longer move the balance.
  - **Your existing imports are repaired by the update**, nothing to delete or redo: accounts that
    had imported rows from on or before the day you added them get that day as their balance date.
    Checked on a copy of real data: both accounts came out exactly at the bank's available balances.
  - The update also tidies payees imported from Example CU's QFX files: "Spotify Date" becomes "Spotify",
    "Freshco # Date" becomes "Freshco", "Wm Supercenter # Main St" becomes "Wm Supercenter".
- **The import screen compares with your bank.** Example CU's files include the balance; Ledger shows it
  next to its own for that day, and "Use the bank's balance" makes them match if they don't.
- **Fix:** QFX files with a $0 "Prenotification" row no longer fail to import; the row is skipped and
  listed, like in CSV files.
- **Example CU's QFX files read better:** payees come in clean (no "Date 09/24/2026" or street address),
  and transfers are recognised from Example CU's wording. "Mobile From Share 0001" is one of your own
  shares, matched to the right account once that share's file has been imported. "Online Banking From
  Pat … 1234XXXXXX Share 0004" has another member's number, so it's someone else's account.
- 467 tests.

## 2.4.1

- **Transfers with someone else's account.** Money moved to or from a family member's account (or
  anyone else's) can now be a transfer instead of income or spending.
  - In the editor, choose **Transfer**, then **"Someone else's account (outside)"** as the other side,
    and whether the money came in or went out. Existing entries, including imported ones, can be
    changed the same way.
  - Your balance moves as usual, but it doesn't count as income or spending anywhere. The list shows
    "Someone else → Checking" or "Checking → someone else", and the Transfers filter includes them.
- **Import suggests it:** rows like "Transfer from Pat…" or "Member-to-Member Transfer Sent to…" that
  don't name one of your accounts are suggested as transfers with someone else's account. Tick
  Remember and they're automatic next time.
- The JSON export is format version 3 (types `outsideIn`, `outsideOut`).
- 453 tests.

## 2.4.0

- **Refunds.** A fourth kind of transaction next to Spending, Income and Transfer.
  - A refund is money back for something you bought. It goes into its account and **lowers
    spending in its category** for the month it arrives. It never counts as income, so returns no
    longer inflate your income figures.
  - Everywhere agrees: Overview, Budget, Reports, Bills forecast and the Transactions list, which
    shows "Spending $X (after $Y in refunds)".
  - If more comes back than was spent in a month, that category shows "(refunds)" and a negative
    amount instead of hiding it.
  - Typing the store's name suggests the category you usually use there.
  - Refunds you recorded as income before can be switched to Refund in the editor.
- **Import suggests refunds:** money coming back from a store you've spent at, or a row that says
  "REFUND" or "RETURN", is suggested as a refund in that store's usual category ("EXAMPLE STORE
  RETURN" becomes "Example Store").
- **Import records bills:** a bank row that pays a bill (same account, within 5 days of the due
  date, the exact amount, or close for an estimated bill with a matching name) shows "Counts as
  the '…' bill due …". Import it and the bill shows as recorded instead of "Needs recording".
  Untick the box to keep them separate. Undoing the import puts the bill back.
- **Fix:** if a bank renames a column in its CSV, import no longer stops at "Choose the date,
  description and amount columns". It detects the new layout and remembers it.
- **Fix:** some bill and paycheck messages showed garbled characters (for example "Ã¢Â€Âœ" instead of
  quotes, and "Ã" instead of "×" in "4 h × $13.50"). A test now stops this from coming back.
- The JSON export is now format version 2 (it can contain `"type": "refund"`); the CSV export shows
  "Refund" with a positive signed amount.
- The rest of V2 is renumbered so versions keep going up: autofill 2.5, reconciliation 2.6, sinking
  funds 2.7, scenarios 2.8.
- 451 tests.

## 2.3.2

Fixes from importing a real Example CU export:

- **Paychecks could have been skipped.** Example CU doesn't put quotes around a comma in a description,
  so a row like "ACH Deposit From RIVERSIDE MARKET, INC." was misread and left out. Those rows now import
  correctly. Example CU's $0 notices (e.g. a "Prenotification" before a first direct deposit) are
  listed as skipped instead of as errors.
- **Transfers between your own accounts** (e.g. "Transfer from Main Savings") are suggested as
  transfers with the right account, instead of as income. Tick "Remember" and the account is
  remembered too.
- **Importing checking and then savings no longer counts transfers twice.** The savings side shows
  as "Probably already in Ledger", even when another withdrawal of the same amount happened that
  day.
- **Cleaner payees:** codes that change every month are dropped, so "Spotify P123456AB1" is just
  "Spotify" and a remembered rule keeps working. The same goes for "Target T-1234" → "Target" and
  "August 2026 Dividend APY Earned 2.700%" → "Dividend APY Earned".

## 2.3.1

- **Colour schemes from 1.9.5** are now part of 2.x: Settings → Appearance → "Colour scheme"
  (Ledger, Frost, Forest, Terracotta). Your choice from 1.9.5 carries over. See 1.9.5 below.

## 2.3.0

- **Import from your bank:** Transactions → "Import from your bank". Download a CSV or QFX file from
  Example CU (or any bank or card) and choose it.
  - **Review before anything is saved:** every row shows a readable payee (e.g. "POS PURCHASE QUICKMART
    0412 RIVERTON PA" becomes "Quickmart Riverton"), a suggested type and category. You can
    change any of them, and untick rows you don't want.
  - **No doubles:** rows already in Ledger (imported before, or typed in by hand) are flagged and
    start unticked. Importing the same file twice adds nothing.
  - **Remember:** tick "Remember" on a row you corrected, and that payee and category are suggested
    for the same bank description next time. Categories are also suggested from how you've filed
    that payee before.
  - **Remembers your file's columns** per account, so the second import is just "choose file".
  - **Undo an import:** recent imports are listed, and "Undo" removes exactly what that import
    added.
  - Money into a credit card is suggested as a payment from another account.
- The JSON export includes imports, remembered columns and payee rules (`docs/EXPORT_FORMAT.md`).
- Not yet: linking an imported row to a bill so it counts as recorded. Until then, record the bill
  as usual; the import will usually flag the bank row as "Probably already in Ledger".

## 2.0.0

- **Paycheck deductions:** in Edit job → "Deductions from pay", add anything your employer takes
  out:
  - health, dental or vision plans (pre-tax);
  - a 401(k), as an amount or a % of pay (pre-tax);
  - after-tax items like a Roth 401(k) or union dues.

  Each lowers exactly the taxes it should. Changing one later never rewrites past estimates.
- **Paystub check:** open a paycheck on the Income page and choose "Enter paystub…".
  - Type what your stub says; only gross and take-home are needed. Ledger compares line by line
    as you type and explains each difference: hours, pay rate, W-4 settings, a pre-tax benefit,
    your local tax rate, or a deduction Ledger doesn't know about.
  - Each paycheck shows "Paystub matches" or how many lines differ.
  - "Record this paycheck" then starts from the paystub's real take-home and pay date.
- **Confirm your real payday** from the first paystub, in one tick. Future paydays and pay periods
  line up with Riverside Market actual schedule, and "Payday not confirmed" goes away.
- Social Security's yearly wage limit now leaves out pre-tax health benefits, as the law does.
- The JSON export includes deductions and paystubs (`docs/EXPORT_FORMAT.md`).
## 1.9.5

- **Colour schemes.** Settings → Appearance now has a "Colour scheme" choice after Theme, with a small
  preview strip for each:
  - **Ledger** (the default): warm off-white, charcoal, dusty blue and sage.
  - **Frost:** frost blue with dusty white.
  - **Forest:** forest green and diorite, with berry as the accent.
  - **Terracotta:** terracotta, redwood and gold.
- Every scheme has a light and a dark version, so it works with System, Light or Dark. Windows high
  contrast still overrides any scheme.
- The scheme changes straight away, without restarting, and Ledger remembers it (it's saved with
  your data, so it's in backups too).
- Money in, problems and warnings stay clearly green, red and amber in every scheme, and "Actual" and
  "Forecast" tags always look different. All text meets WCAG contrast (4.5:1), checked by tests.
- The accent colour follows the scheme everywhere: buttons, switches, progress bars, the selected
  page, links, and the window's minimize/maximize/close buttons.
- Small contrast fix in the default scheme's light theme: faint helper text and the "money in" green
  are slightly darker, so they now meet 4.5:1 on the page background.
- 390 tests.

## 1.9.0: V1 complete

- **Phase 12: tests and the definition of done.** All twelve phases of the brief are done. See
  `docs/V1.md`.
  - New guarantees, each tested:
    - Looking at forecasts, reports or exports never changes stored data.
    - Money is conserved, and transfers never change net worth (randomized over thousands of
      transactions).
    - The Dashboard, Budget, Reports and Transactions pages always agree.
    - A deleted transaction counts nowhere.
    - Goal math can't produce invalid numbers.
    - The database has the required indexes and constraints.
  - `scripts/smoke-test.ps1`: a one-command click-through of the app on a throwaway database.
- **Fix:** paycheck details now take overtime pay from the payroll calculation. The window used to
  recalculate it with the end-of-period rate, which split pay wrongly after a mid-week raise.
- 353 tests.

## 1.8.0

- **Polish** (Phase 11), from an audit of every page in light and dark theme, at the smallest window
  size, and on a brand-new database.
  - **Reports** is no longer a placeholder: spending by category for any month, six months of
    cash flow (income vs spending, with the net), net worth at each month end, and account
    balances. It uses plain numbers and bars; every figure matches the other pages.
  - **Narrow windows:** below about 1,100 px the sidebar shrinks to icons (the menu button opens
    it), so pages keep their room. On Bills, the forecast pickers sit under their heading.
  - **Ledger remembers its window** size, position and maximized state.
  - Fixed large blank gaps at the top of pages (hidden notices still took up space).
  - Fixed the Income page's "By payday / By work date" choice overlapping its caption.
- **Smaller install: 177 MB → 100 MB** (175 files), by trimming unused framework code. It was
  verified end to end on the published build:
  - every page and editor;
  - a new database, and upgrading a 1.3 database;
  - backup, restore, CSV and JSON export.

## 1.7.0

- **Backup and export** (Phase 10), in Settings → Data.
  - **Back up now:** save a complete copy of your data (one `.db` file) anywhere, such as a USB
    drive or cloud folder.
  - **Automatic daily backup** when Ledger opens, keeping the newest 10 (can be turned off).
  - **Restore a backup:**
    - Ledger checks the file first (intact, really a Ledger backup, not from a newer version) and
      shows what's in it.
    - It saves your current data before replacing it, so a restore can be undone.
    - An older backup is upgraded, and then Ledger restarts.
  - **Export transactions (CSV)** for spreadsheets. Text that looks like a formula is neutralized,
    so an odd payee can't run anything in Excel.
  - **Export everything (JSON)** in a documented, versioned format (`docs/EXPORT_FORMAT.md`).
  - "Open data folder" and "Open backups folder".
- File dialogs use the Windows App SDK pickers, which work reliably for Ledger's type of install.

## 1.6.1

- **Smaller install: 240 MB / 543 files → 177 MB / 306 files.**
  - Windows App SDK is referenced by component. The all-in-one package also bundled Windows AI,
    machine learning, Search and Widgets, about 55 MB Ledger never used.
  - English resources only (drops 83 translation folders).
  - No debug symbols or debugger helper in the installed copy.
- The dashboard reads the forecast once instead of twice.
- A paycheck whose recorded transaction was deleted now counts as unrecorded (like bills), and can
  be recorded again. This also removes a warning logged at every launch.

## 1.6.0

- **Overview dashboard** (Phase 9), answering the brief's five questions at a glance:
  - **Available:** actual money in every account except credit cards, with net worth.
  - **This month so far:** income, spending, saved (money moved into savings accounts, minus any
    moved back out) and what's remaining. Transfers are never income or spending.
  - **Next two weeks:** the forecast's next bills, income and paychecks, tagged "Forecast". It warns
    if the balance would go below zero, and links to bills that need recording.
  - **Budget:** planned groups with spent vs. plan, over-budget groups first.
  - **Goals:** progress bars with the suggested monthly amount.
  - Every figure comes from the same services as its own page. The only new calculation is
    "Saved", and it's tested.

## 1.5.1a

- **Nothing drops off while Ledger is closed.** A missed bill stays under "Needs recording" until you
  record or skip it, however long the app was closed. Before, bills dropped off after 31 days.
  An unrecorded paycheck stays expected, and stays listed on the Income page, until you record it.
  Before, it dropped off after 14 days.
- **Skip all** clears a backlog of missed bills in one step.
- **Record automatically** (per bill, fixed amounts only). Each due date from the day you turn it
  on is recorded as a real transaction, even if Ledger was closed that day. It runs when the app
  opens and when you open Bills.
  - Each date is recorded at most once.
  - Automatic entries are tagged "Automatic" in Transactions, and the Bills page lists what was
    just recorded.
  - Deleting an automatic entry sends that date back to "Needs recording" instead of recreating it.
  - If a date can't be recorded (for example, its account was archived), you're told why and it
    waits for you.
  - Paychecks are never recorded automatically, because the real amount always differs a little.
- The version shown in Settings → About can now carry a letter (1.5.1a).

## 1.5.0

- **Goals** (Phase 8): savings goals as calm cards: amount saved of the target, a progress bar, the
  percent, the target date and a suggested monthly amount.
  - Track progress yourself ("Add money" and "Take money out", with a history you can correct),
    or have the goal follow a dedicated savings account's real balance.
  - An account can back only one goal, so the same money is never counted twice. Credit cards
    can't back a goal.
  - The suggested amount assumes you save now and then monthly until the target date, rounded up
    so it's never short. It's only a suggestion: nothing moves automatically, and adding money to a
    goal never changes an account balance.
  - Goals can be archived and restored, or deleted.
  - The percent never shows 100% before the goal is actually reached.

## 1.4.1

- Fixed a crash when choosing a single account (such as Savings) in the Bills forecast. Changing the
  account or the forecast length now updates only the forecast.

## 1.4.0

- **Bills** (Phase 7: recurring transactions and the forecast).
  - Add bills, subscriptions, recurring income and recurring transfers: weekly, every two weeks,
    monthly, every three months or yearly. The amount can be marked as an estimate.
  - Monthly bills on the 29th–31st fall on the last day of shorter months, then return.
  - **Coming up:** your actual balance today, then every expected event over the next 14–90 days,
    with the balance each would leave.
    - Expected events come from bills and income, transactions already recorded with a future
      date, and predicted Riverside Market paychecks.
    - Forecast amounts are tagged "Forecast" and shown in italics. Only today's balance is tagged
      "Actual".
    - The lowest point is shown, with a warning in words when the balance would go below zero.
    - Covers all accounts except credit cards, or a single account you pick.
  - **Nothing is recorded automatically.** "Record" opens a pre-filled transaction for you to
    confirm. Each date can be recorded once.
  - Skip a date that won't happen, or pause a bill.
  - **Needs recording:** dates in the last month that passed without being recorded.
  - Rule list with the next date, a "per month" figure for anything not monthly, and monthly totals.
  - A bill that has recorded payments can be paused but not deleted, so your history stays linked.
- New `docs/RULES.md`: every rule behind the numbers, with the tests that enforce it.

## 1.3.1

- **Automatic breaks.** Each job has a break rule (default: a paid 15-minute break on every shift, plus
  an unpaid 30-minute break on shifts of 5 hours or longer).
  - Shift hours are now the length as scheduled. Paid time is worked out automatically, so a
    6-hour shift pays 5.5 hours.
  - The shift editor shows paid time live, and a single shift can use a different break.
  - The paste preview and shift list show paid hours, with the break noted.
- Existing shifts were converted automatically. Breaks you had entered are kept exactly.
- **Safety:** before any database upgrade, Ledger now saves a full copy of your data to
  `Backups\ledger-before-<upgrade>-<time>.db`.

## 1.3.0

- **Income Calculator:** a new Income page that predicts your paychecks.
  - Add a job with its hourly rate, pay schedule, W-4 choices and work location.
  - Paste the schedule your employer shows you (for example "29th: 4hrs, Oct. 2nd: 6hrs",
    or "Tue 9/29 4:00 PM - 8:00 PM"). A preview comes first, and unreadable lines are listed,
    never dropped. Re-pasting updates rather than duplicates.
  - You can also add shifts one at a time (hours, or start and end times with a break) and mark
    them worked.
  - Each paycheck is estimated with a paystub-style breakdown:
    - Federal withholding (IRS 2026 tables)
    - Social Security and Medicare
    - PA income tax 3.07%
    - PA unemployment 0.07%
    - Local earned income tax (the higher of home and work, Act 32)
    - Local services tax
  - Overtime is 1.5× over 40 hours per workweek.
  - Totals for this week, the next two weeks and this month, counted by payday or by work date.
  - "Record this paycheck" opens a pre-filled income entry. The estimate is never overwritten,
    and the recorded amount is shown against it.
  - Home local tax rate in Settings (default: Maple Boro, 1.5%).
- Tax reference data lives in `Data/Reference/tax-2026.json`.

## 1.2.0

- **Transactions:** record spending, income and transfers.
  - A fast entry dialog, with "Save and add another" for entering several in a row.
  - Past payees are suggested as you type, and choosing one fills in its category.
  - The last account you used is remembered.
  - Pending transactions are marked.
  - The list is grouped by date, with a search box that matches payee, note, category and amount.
  - Filters for date range, account, category and type.
  - A summary line shows the count, income and spending.
  - Delete takes effect straight away, with an Undo option.
  - Shortcuts: Ctrl+N adds a transaction, Ctrl+F jumps to search.
  - Transfers and credit card payments are never counted as income or spending.
  - On the Accounts page, "View transactions" opens the list filtered to that account.
- **Budget:** monthly plans for spending groups.
  - Planned, spent and remaining for the month, with a bar for each group.
  - Each group shows On track, "nearly used" or "over", always in words as well as colour.
  - Spending with no category is shown rather than hidden.
  - Move between months, or copy last month's plan into an empty month.
  - Spent always comes from your actual transactions. Editing a plan never changes it.

## 1.1.0

- **Categories:** a new Settings → Categories page to add, rename, group, move and archive
  spending, income and savings categories.
  - Categories are at most two levels deep (group → subcategory), and a subcategory always
    shares its group's kind.
  - Names are unique within a group, ignoring case.
  - Archiving a group archives its subcategories. Restoring a subcategory restores its group.
  - Categories are archived, never deleted, so past transactions keep them.
- Clicking Settings in the navigation rail now returns to Settings from the Categories page.

## 1.0.0

- Foundation: WinUI 3 shell, SQLite database with migrations, default categories, light/dark theme.
- Accounts: add, edit, archive and restore accounts. Balances and net worth are derived from history.
- First-launch setup: welcome, first account, ready.
- App icon, and an install script that creates a desktop shortcut.
- Fixed page content running off the right edge when the window is wide or maximized.
- Data safety: only one copy of Ledger runs at a time, the database is saved fully on close, and
  development builds use a separate data folder.

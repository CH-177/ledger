# Ledger's rules

These are the rules Ledger's numbers follow, in plain language. Each rule names the tests
(in `tests/Ledger.Core.Tests`) that enforce it. Those tests are the specification: any change to
the app, or a port to another platform (see `PORTABILITY.md`), has to keep them passing.

## Money

- Amounts are whole cents stored as integers, never floating point. Displayed amounts round
  half away from zero. *(MoneyTests)*
- A stored amount is always positive; whether it's money in or out comes from the kind of
  transaction. *(DatabaseTests.Zero_or_negative_amounts_are_rejected_by_the_database,
  TransactionServiceTests.Amount_must_be_positive)*
- Amounts over $999,999,999.99 and dates outside 1900–2100 are rejected.
  *(TransactionServiceTests.Amount_over_the_cap_is_rejected, …Dates_outside_a_sensible_range_are_rejected)*

## Balances and net worth

- A balance is never stored. It is the starting balance plus every non-deleted transaction dated
  on or before today. *(AccountServiceTests.Income_adds_and_expenses_subtract)*
- **Balance date.** A starting balance is the bank's balance at the end of a day (new accounts:
  the day they're added). Transactions dated on or before that day are already inside it: they
  count as income and spending history but don't move the balance again, so importing older bank
  rows never double-counts. Balance on day D = starting balance + movements up to D − movements up
  to the balance date. Accounts from before 2.4.2 have no balance date (the starting balance is
  from before every transaction), except that the 2.4.2 upgrade gave one (the day the account was
  added) to accounts that had imported rows dated on or before that day.
  *(BalanceDateTests, MigrationTests.AccountBalanceDate_upgrade_stops_imports_from_counting_twice)*
- **Using the bank's balance** from an import (its closing balance, or the running balance after
  the file's last posted day) sets the starting balance and balance date to exactly that, so Ledger
  matches the bank on that day whatever is imported later. Credit cards are left out, because banks
  sign card balances differently. *(BalanceDateTests)*
- Future-dated transactions are real but not yet in the balance.
  *(AccountServiceTests.Future_dated_transactions_are_not_part_of_the_actual_balance)*
- A credit card balance is money owed (a liability). A card purchase counts once, when it's made;
  paying the card is a transfer. *(AccountServiceTests.Credit_card_purchase_and_payment_are_counted_once)*
- Net worth is assets minus liabilities, over accounts marked as included. Archived accounts still
  count. *(AccountServiceTests.Net_worth_excludes_accounts_flagged_out_but_keeps_archived_ones)*
- Editing an account's starting balance never rewrites its history.
  *(AccountServiceTests.Update_changes_details_and_rebases_balance_without_touching_history)*

## Transactions

- A transfer is one row that moves money between two different accounts. It is never income or
  spending, and never changes net worth.
  *(AccountServiceTests.Transfer_moves_money_without_changing_net_worth,
  TransactionServiceTests.Income_and_transfers_are_not_spending, DatabaseTests.Transfer_requires_a_different_destination_account)*
- **Refunds** are money coming back for something bought. A refund adds to its account and lowers
  spending in its category, in the month it arrives (cash basis): a refund in October for a
  September purchase lowers October. A category's spending can go below zero, and is shown as
  "(refunds)". Refunds never count as income anywhere, and are never scheduled as bills.
  *(RefundTests, InvariantTests)*
- **Transfers with someone else's account** (a family member's, say) are neither income nor
  spending. They change the account's balance and net worth, since the money really arrived or
  left. The Transfers filter includes them. They aren't scheduled as bills yet.
  *(OutsideTransferTests, InvariantTests)*
- A category must match the kind of transaction: spending (also for refunds), income, or savings for transfers.
  *(TransactionServiceTests.Category_must_match_the_kind_of_transaction)*
- Archived accounts and categories can't be chosen for new entries, but existing entries keep them.
  *(TransactionServiceTests.Archived_accounts_and_categories_cannot_be_newly_used_but_existing_entries_can_be_edited)*
- Deleting a transaction hides it from every total but keeps it on disk, so it can be undone.
  *(TransactionServiceTests.Delete_hides_the_transaction_and_restore_brings_it_back, DatabaseTests.Soft_deleted_transactions_are_hidden_but_retained)*
- An account with history can't be deleted, only archived. *(DatabaseTests.Accounts_with_history_cannot_be_hard_deleted)*

## Categories

- There are at most two levels, group › subcategory, and a subcategory has its group's kind.
  *(CategoryServiceTests.Categories_are_at_most_two_levels_deep, …Subcategory_inherits_its_parents_type_and_goes_last)*
- Names are unique within a group, ignoring case. Archived names still count as taken.
  *(CategoryServiceTests.Sibling_names_must_be_unique_ignoring_case, …Archived_names_still_count_as_taken)*
- Categories are archived, never deleted. Archiving never changes transactions.
  *(CategoryServiceTests.Archiving_never_touches_transactions_that_use_the_category)*

## Budgets

- A budget plans money per top-level spending group per month. Spent is always computed from
  actual transactions, and editing a plan never changes it.
  *(BudgetServiceTests.Editing_a_plan_never_changes_actual_spending)*
- Spending in a subcategory counts toward its group. Uncategorised spending is shown, not hidden.
  *(BudgetServiceTests.Subcategory_spending_rolls_up_into_its_group, …Uncategorised_spending_is_shown_not_hidden)*
- Income and transfers never count as spending, and a card purchase counts once.
  *(BudgetServiceTests.Income_and_transfers_never_count_as_spending, …Card_purchase_counts_once_and_the_payment_not_at_all)*
- "Nearly used" starts at 90% of the plan. *(BudgetServiceTests.Status_thresholds)*

## Recurring bills and income

- A recurring rule never creates a transaction by itself unless it is set to record automatically
  (below). Its dates are computed from its first
  date each time, so opening the app can't advance or duplicate anything.
  *(RecurringServiceTests.Upcoming_occurrences_are_forecasts_and_nothing_is_created)*
- The nth date is the first date plus n periods. Monthly rules on the 29th–31st use the last day
  of shorter months and return afterwards. Yearly rules on Feb 29 use Feb 28 in other years.
  *(RecurrenceScheduleTests)*
- Each scheduled date can be recorded once, by linking one real transaction to it. Deleting that
  transaction frees the date again.
  *(RecurringServiceTests.Recording_links_a_transaction_once_and_a_deleted_one_frees_the_date,
  DatabaseTests.A_recurring_occurrence_can_only_be_posted_once)*
- A skipped date leaves the forecast. Recording a skipped date un-skips it.
  *(RecurringServiceTests.Skipping_removes_an_occurrence_until_undone_and_recording_clears_a_skip)*
- A date that passed without being recorded asks to be recorded until it is recorded or skipped,
  however long the app was closed (worked out up to a year back). It never asks about dates before
  the rule was added.
  *(RecurringServiceTests.Missed_occurrences_stay_until_handled_however_long_the_app_was_closed_but_never_before_the_rule_was_added)*
- A rule set to record automatically records each due date, from the day that was turned on, as a
  real transaction, once. It never records estimated amounts, skipped dates, or a date whose
  transaction the user deleted. Paychecks are never recorded automatically.
  *(RecurringServiceTests.Auto_record_files_each_due_date_since_it_was_turned_on_exactly_once,
  …Deleting_an_automatic_entry_sends_that_date_back_to_needs_recording_instead_of_recreating_it,
  …Skipped_dates_paused_rules_and_future_dates_are_not_auto_recorded, …Estimated_amounts_cannot_be_auto_recorded)*
- A rule with recorded payments can be paused but not deleted.
  *(RecurringServiceTests.Only_a_rule_with_no_recorded_payments_can_be_deleted)*
- The "per month" figure is weekly × 52 ÷ 12, every two weeks × 26 ÷ 12, quarterly ÷ 3, and
  yearly ÷ 12. *(RecurrenceScheduleTests.Monthly_equivalent)*

## Forecast

- The forecast starts from the actual balance today. Everything after it is expected, never money
  that exists, and is shown as a forecast.
  *(ForecastServiceTests.Projects_the_balance_event_by_event_from_the_actual_balance)*
- By default it covers every open account except credit cards. Moves between two covered
  accounts cancel out.
  *(ForecastServiceTests.Transfers_count_only_when_money_leaves_or_enters_the_covered_accounts)*
- Expected events come from three sources:
  - recurring dates not yet recorded or skipped;
  - transactions recorded with a future date;
  - predicted paychecks until the real paycheck is recorded.

  *(ForecastServiceTests.Future_dated_transactions_are_included_but_not_counted_twice,
  …Predicted_paychecks_are_expected_income_until_recorded)*
- Missed, unrecorded dates are placed on today. *(ForecastServiceTests.Recorded_and_skipped_occurrences_leave_the_forecast_and_missed_ones_land_on_today)*
- On the same day, money out comes before money in, so the lowest point is never understated.
  *(ForecastServiceTests.Money_out_comes_before_money_in_on_the_same_day)*

## Goals

- Progress is derived, never stored as a running total. A manual goal is its starting amount plus
  the amounts added and taken out. An account-backed goal is that account's actual balance.
  *(GoalServiceTests.A_manual_goal_is_its_starting_amount_plus_contributions,
  …An_account_backed_goal_follows_the_actual_balance)*
- Adding money to a goal is an earmark. It never changes an account balance or creates a transaction.
  *(GoalServiceTests.Contributions_never_change_account_balances)*
- A goal can't go below zero, and each account backs at most one active goal.
  *(GoalServiceTests.Taking_out_more_than_is_saved_is_refused, …Each_account_backs_at_most_one_goal_and_never_a_credit_card)*
- Percent is rounded down and capped at 100. The suggested monthly amount is the remaining amount
  divided by the monthly contributions that fit before the target date (starting today), rounded up
  to the cent. There is no suggestion without a date, after the date, or once the goal is reached.
  *(GoalMathTests)*

## Paycheck deductions and paystubs

- **Which taxes each deduction lowers:**
  - A pre-tax health, dental or vision plan (Section 125) lowers federal, Social Security, Medicare,
    PA and local tax, but not PA unemployment.
  - A pre-tax 401(k) lowers federal tax only; PA and local tax still apply.
  - After-tax deductions change take-home only.

  Deductions never take more than you earned: retirement, then health, shrink to fit. Take-home
  never goes below zero. *(PaycheckDeductionTests)*
- A percentage deduction is that share of each paycheck's gross, rounded to the cent.
- **Changing a deduction keeps history.** A deduction applies to pay periods ending from its start
  date. Changing one that has already applied ends it yesterday and starts a new version today, so
  earlier estimates never change. *(DeductionAndPaystubServiceTests)*
- **Paystub comparison:** each tax line is compared with Ledger's calculation re-run on the
  paystub's own gross. Differences of up to 2¢ count as matching.
  - Hours or rate differences are blamed on gross.
  - A federal difference points to the W-4; a lower Social Security or PA base points to a
    pre-tax benefit.
  - The local tax's implied rate is shown.
  - Money unaccounted for points to a deduction Ledger doesn't know about.
  - A paystub never changes the estimate or any transaction. *(DeductionAndPaystubServiceTests)*
- **Confirming the payday from a paystub** lines pay periods up with the stub. Payday becomes the
  stub's pay date minus its period end, and the job is marked confirmed.

## Bank import

- **Nothing is saved until you press Import.** Opening a file and reviewing it change nothing. The
  file is only read, never copied. *(ImportTests.Rows_become_suggested_spending_and_income_and_nothing_is_saved_by_previewing)*
- **Files:** QFX/OFX (SGML or XML) and CSV. A CSV's columns are detected from the header when
  possible: one signed amount, or separate debit and credit columns. You can change the mapping,
  and it is remembered per account. *(…The_csv_layout_is_remembered_per_account)*
  - A row with more fields than the header (a comma left unquoted in the description, as Example CU
    does) keeps the extra fields in the description. $0 rows (bank notices) are listed and skipped.
    *(…A_credit_union_style_export_with_unquoted_commas_and_times_is_read)*
- **Suggestions:** money out is spending and money in is income. Money into a credit card is
  suggested as a payment transfer, and you choose where it came from. The payee is cleaned from the
  bank's description: card numbers, store and reference codes, account numbers in brackets, "August
  2026"-style months and "POS PURCHASE"-style prefixes are removed, so the same payee reads the same
  every month. A payee you ticked "Remember" for wins, including the other account for a transfer.
  Otherwise the category is the one that payee was filed under most often.
- **Refunds:** money coming in is suggested as a refund when the description says so ("REFUND",
  "RETURN") or when you've spent at that exact payee before; the category is the one you usually
  use there. On a card, the same applies; other money in stays a card payment.
  *(…Money_back_from_a_store_you_spend_at_is_suggested_as_a_refund_not_income)*
- **Bills:** a new row counts as the payment of an unrecorded bill (or expected income) on the same
  account when it's within 5 days of the due date and either the amount is exact, or the bill is
  estimated, the amount is within 20% and the payee looks like the bill's name. The review shows
  "Counts as the … bill due …", which you can untick. Undoing the import puts the bill back to
  "needs recording". *(…A_bank_row_that_pays_a_bill_is_linked_so_the_bill_counts_as_recorded)*
- **Paychecks:** a new income row in a job's deposit account counts as that job's predicted, not yet
  recorded paycheck when it's within 5 days of the payday and the payee looks like the job's name or
  the amount is within 25% of the estimate. Importing it records the paycheck exactly as "Record this
  paycheck" does (the estimate is kept for comparison and stops counting in the forecast); undoing
  the import makes it an estimate again. *(…An_imported_paycheck_deposit_counts_as_the_predicted_paycheck)*
- **Example CU shares:** Example CU names shares, not accounts. "Share 0001" in a transfer is one of your own
  shares, matched to the account whose imported file had that share (its bank account id ends in
  "S0001"); until that file has been imported, the row is suggested as a transfer for you to point at
  the right account. A transfer that also shows another member's masked number ("1234XXXXXX") is with
  someone else's account. *(OutsideTransferTests.Credit_union_qfx_transfers_are_told_apart_by_share_and_member_number)*
- **Transfers between your own accounts:** a "Transfer from/to …" description that names exactly
  one of your other accounts is suggested as a transfer with that account. Other "Transfer from/to"
  rows that name none of your accounts are suggested as transfers with someone else's account
  ("Transfer from Pat…", "Member-to-Member Transfer Sent to…"), with a hint; if they name more
  than one, they keep income or spending. Remember keeps the choice, including "someone else's".
  *(OutsideTransferTests)*
  *(…Transfers_between_your_own_accounts_are_suggested_remembered_and_not_imported_twice)* *(…Bank_descriptions_become_readable_payees, …A_remembered_correction_is_suggested_next_time_and_history_suggests_categories)*
- **Duplicates:**
  - Same bank transaction ID already in that account means "Already imported". These rows are
    skipped even if ticked, so importing the same file twice adds nothing.
  - Otherwise, a transaction with the same amount and direction in that account counts as
    "Probably already in Ledger" when it is on the same day, or within 3 days with a similar payee
    (or no payee). Each existing transaction matches at most one row, and rows with a similar payee
    are paired first. Likely duplicates start unticked; you decide.
  - So after importing checking, the savings side of the same transfers shows as already in Ledger.
  *(…Importing_saves_one_batch_and_the_same_file_twice_adds_nothing, …Something_already_typed_in_by_hand_is_flagged_likely_and_matched_only_once)*
- **All or nothing:** the ticked rows are saved together as one import, marked cleared. If any row
  is invalid, nothing is saved. *(…A_bad_row_saves_nothing_at_all)*
- **Undo** deletes exactly the transactions that import added (a normal delete) and nothing you
  typed yourself. The same file can then be imported again. *(…Undo_removes_exactly_that_import_and_the_file_can_be_imported_again)*

## Reconciliation

- **The statement is the truth.** Reconciling an account with a statement compares the statement's
  ending balance with Ledger's *reconciled balance* (the starting balance plus every reconciled
  transaction dated up to the statement date) plus the transactions you tick. The difference is
  statement minus that; **Finish** works only at exactly $0.00, and marks the ticked transactions
  Reconciled. *(ReconciliationTests.The_difference_follows_every_tick_and_only_zero_finishes_then_the_next_statement_starts_from_it)*
- **What's offered:** the account's unreconciled transactions up to the statement date, plus a week
  after it (marked; banks sometimes post early). Transactions on or before the account's balance date
  aren't offered: they're already in the starting balance. Cleared ones on or before the date start
  ticked; pending and later ones don't. A transfer's effect is out of its source and into its
  destination, so it's reconciled once, from whichever side goes first.
  *(…Transactions_inside_the_starting_balance_are_not_offered_and_transfers_count_on_both_sides)*
- **Cards:** the statement balance is entered as the amount owed. *(…A_card_statement_is_money_owed)*
- **Hints** when it's off: a transaction whose ticking or unticking fixes it exactly (noting if it's
  pending or dated after the statement), or, when the difference divides by 9, a ticked amount that
  matches with two neighbouring digits swapped. *(…Hints_point_at_a_swapped_amount_a_late_posting_and_something_not_on_the_statement)*
- **Undo** takes back the latest reconciliation only: its transactions go back to Cleared.
- **Reconciled transactions are protected.** Saving one keeps it reconciled; changing its amount,
  date, kind or account asks for a second Save, and deleting it asks first.

## Sinking funds

- **A sinking fund is a goal for money set aside** for a known cost (car repairs, gifts, a yearly
  bill). It's always tracked by hand: what it holds is its starting amount plus money set aside, minus
  spending paid from it. Setting money aside never moves money between accounts.
  *(FundsAndScenarioTests.A_fund_is_set_aside_monthly_spent_from_and_never_goes_below_zero)*
- **Paying from a fund:** spending can be paid from a fund; it still counts as spending in its
  category and moves its account as usual. A fund can't go below zero (editing gives the transaction's
  own amount back first). Deleting the spending gives the money back to the fund; deleting the fund
  leaves the spending as plain spending. Only spending can be paid from a fund.
  *(…A_fund_tracks_its_own_money_and_pays_for_spending_only)*
- **Offered, not assumed:** for a new entry in the category a fund pays for, the fund is pre-selected
  in the editor, where it can be changed. Existing entries are never switched by themselves.
- **Monthly set-aside:** the Budget page lists funds beside the plan (not in it: setting aside isn't
  spending) and offers "Set aside $X" until this month's amount is in.
- **Free to spend** = Available − what all funds hold, shown beneath Available on the Overview.
  Available keeps its meaning. If more is set aside than you have, it says so (warned, not blocked).

## What if? (scenarios)

- **Nothing in a scenario is ever real data.** A scenario is a list of assumptions applied to the
  forecast: a one-off purchase or money in, hours at a percentage of the schedule or a set number of
  paid hours a week, a new pay rate, a new recurring bill or income, or stopping one. Only saved
  scenarios' assumptions are stored (as a preference). "Make it real" opens the usual editor filled in;
  only saving there creates anything. *(InvariantTests.Forecasts_reports_and_every_other_read_never_change_stored_data)*
- **Same math as the forecast:** hours and pay changes re-price paychecks through the payroll
  calculator (breaks, overtime over 40 hours, taxes); set weekly hours are paid hours over four shifts.
  *(FundsAndScenarioTests.Hours_and_pay_changes_re_price_paychecks_with_taxes_and_overtime)*
- **Compare** shows, as things are and with the changes: the lowest balance and its date, the balance
  at the end, the first day below $0 and below a cushion you set; and for goals that follow an account
  with a target date in range, what that account would hold on it.
- **Can I afford this?** runs the forecast with the purchase until 60 days after it: "Yes" if the
  balance never goes below zero (or the cushion); otherwise it says when and how low.
  *(…A_purchase_scenario_changes_only_the_scenario_forecast_and_can_i_afford_it_says_so_plainly)*

## Suggestions

- **Suggestions never save anything.** They fill fields you can see, or offer choices; only Save
  writes. Everything is computed from your own transactions on this PC.
- **Payee habits** come from that payee's latest 12 entries of the same kind (any letter case; a
  refund learns from spending there), and need at least 2. The usual category and account are the
  most used among them, a tie going to the most recent; the typical amount is the median, and the
  range shown is the middle of the amounts. The usual account is filled in only for a new entry
  whose account you haven't changed; the category only when none is chosen.
  *(SuggestionTests.Habits_come_from_recent_entries_in_any_letter_case_and_a_one_off_teaches_nothing)*
- **Payee list:** the most used payees first, then the most recent, each with its usual category.
  *(SuggestionTests.Payee_suggestions_list_the_most_used_first_with_their_usual_category)*
- **Notes** are learned across all payees: notes containing what you've typed, the most used first,
  then the most recent. *(SuggestionTests.Notes_are_suggested_from_any_payee_most_used_first)*
- **Repeats:** spending or income with the same payee (any letter case) and account, not already
  recorded for a bill, over the last 400 days. A repeat is at least 3 entries in a row up to the
  latest one that are 6–8 days apart (weekly), 12–16 days (every two weeks) or 25–36 days apart and
  within 3 days of their usual day of the month (monthly; the 31st and a 30-day month's 30th are the
  same day), with every amount within 20% of the median. It isn't offered when its next date is more
  than 3 days overdue (it stopped), when an active bill of the same kind and account has a similar
  name, when you chose "Don't suggest", or when spotting is off in Settings. The suggested bill
  starts on the next expected date, so past entries never show as "needs recording".
  *(SuggestionTests)*

## Dashboard

- "Available" is the actual balance of every open account except credit cards, the forecast's
  starting point. "This month" counts actual transactions from the 1st through today. Saved is
  money moved into savings accounts from other accounts, minus money moved back out.
  Remaining = income − spending − saved. *(DashboardServiceTests)*

## Reports

- Spending by category uses the budget's spent figures for the month. Cash flow is actual income vs
  spending per calendar month (the current month through today; transfers excluded). Net worth is
  taken at each month end, and today for the current month. *(ReportServiceTests)*

## Paychecks (Income Calculator)

- Paid time is the scheduled shift minus the job's unpaid break; paid breaks don't reduce pay.
  *(BreakRuleTests)*
- Overtime is 1.5× over 40 hours per workweek. Withholding follows IRS Pub 15-T, FICA, PA and local
  rules for the tax year in `Data/Reference/tax-YYYY.json`.
  *(PayrollTaxCalculatorTests, PayScheduleTests, IncomeForecastTests)*
- **Home location:** a fresh install has none (a 0% resident rate, so the work location's rate applies
  until the user enters theirs). Upgrading never changes a saved one.
  *(IncomeForecastTests.Home_starts_unset_and_can_be_changed,
  MigrationTests.HomeTaxDefault_upgrade_leaves_settings_as_they_are)*
- **Outside Pennsylvania** (Settings → Paycheck taxes), only federal income tax, Social Security and
  Medicare are estimated, each exactly as for a Pennsylvania paycheck.
  *(PayrollTaxCalculatorTests.Outside_pennsylvania_only_federal_and_fica_are_estimated)*
- A predicted paycheck is never overwritten by the real one. The real amount is recorded as income
  and shown next to the estimate. *(IncomeForecastTests)*

## Data safety

- A backup is a complete SQLite copy made while Ledger is running (nothing is lost mid-write).
  Restore checks integrity, refuses non-Ledger files and backups from newer versions, saves the
  current data first, and upgrades older backups. Automatic backups run at most once every 20 hours
  and keep the newest 10. *(BackupServiceTests)*
- Exports: CSV neutralizes formula-like text. JSON is versioned, uses integer cents, and includes
  deleted transactions flagged as such. *(ExportServiceTests, docs/EXPORT_FORMAT.md)*
- Before any schema upgrade, a full copy of the database is saved to `Backups`. If the copy fails,
  the upgrade doesn't run. *(MigrationTests)*
- Upgrades keep every existing record. *(MigrationTests.ShiftBreaks_upgrade_keeps_every_shift_and_converts_hours_correctly)*

## Data location

- The installed app keeps its data in `%LOCALAPPDATA%\Ledger`; debug builds in `Ledger-Dev`;
  `LEDGER_DATA_DIR` overrides both. A `Ledger.portable` file beside `Ledger.exe` puts the data in a
  `Data` folder next to it. Installing, updating and uninstalling never touch the data.

## Appearance

- The colour scheme (Ledger, Frost, Forest, Terracotta) is stored under `Appearance.Scheme`,
  separately from the theme (`Appearance.Theme`). A missing or unknown value means Ledger.
  *(ColorSchemeTests.Missing_or_unknown_values_mean_the_default_scheme, …Scheme_choice_persists_in_the_settings_table)*
- Every scheme defines the same colours, in light and dark, and keeps the high-contrast colours
  unchanged. Text is at least 4.5:1 against pages, cards and tags; accent fills at least 3:1.
  Positive stays green, negative red and warning amber, and "Actual" and "Forecast" tags stay
  clearly different. *(ColorSchemeTests)*

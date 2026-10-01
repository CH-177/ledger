# Ledger export format

Ledger never locks your data in. There are three ways to take it anywhere:

| What | Where in the app | Format | Best for |
|---|---|---|---|
| **Backup** | Settings → Data → Back up now | A complete SQLite database (`.db`) | Restoring into Ledger; any SQLite tool can open it |
| **Transactions CSV** | Settings → Data → Export transactions | CSV, UTF-8 with BOM | Spreadsheets (Excel, Numbers, Google Sheets) |
| **Everything JSON** | Settings → Data → Export everything | JSON, described below | Other apps, including a future Swift version of Ledger |

The code is in `src/Ledger.Core/Services/ExportService.cs` and `BackupService.cs`. The format is
checked by `tests/Ledger.Core.Tests/BackupAndExportTests.cs`.

## Transactions CSV

- One row per transaction, oldest first. Deleted transactions are not included.
- RFC 4180: fields with commas, quotes or line breaks are quoted, and quotes are doubled. Lines end
  with CRLF.
- Columns:

| Column | Example | Notes |
|---|---|---|
| Date | `2026-09-26` | ISO 8601 |
| Type | `Spending`, `Income`, `Transfer`, `Refund`, `Transfer in (outside)`, `Transfer out (outside)` | Refund added in 2.4, outside transfers in 2.4.1 |
| Payee | `Quickmart` | |
| Category | `Food › Groceries` | Group › subcategory, or just the group |
| Account | `Checking` | For a transfer, the account money left |
| To account | `Savings` | Transfers only |
| Amount | `8.42` | Always positive, dot decimal, two places |
| Signed amount | `-8.42` | + income, refunds and transfers in from outside; − spending and transfers out to outside; blank for transfers between your accounts |
| Status | `Cleared`, `Pending`, `Reconciled` | |
| Note | | |
| Recorded automatically | `Yes` / `No` | Filed by a bill set to record automatically |

- Text that a spreadsheet would run as a formula (starting with `=`, `+`, `-`, `@`, tab or carriage
  return) is written with a leading `'`, so an odd payee name can never execute anything.

## Everything JSON (format version 3)

One JSON object. Conventions:

- **Money is integer cents.** Every field ending in `Cents` is a whole number of cents (`16312` =
  $163.12). Amounts are positive magnitudes; `type` gives the direction, exactly as in the app.
- **Dates** are `yyyy-MM-dd`; **times of day** are `HH:mm:ss`. **Timestamps** (`createdAt`, `updatedAt`,
  `deletedAt`, `exportedAt`) are ISO 8601 in UTC, written without a zone suffix (`2026-09-27T16:00:00`).
- **Enums are camelCase words** (`"checking"`, `"expense"`, `"monthly"`), never numbers.
- **Records refer to each other by `id`** (`accountId`, `categoryId`, …). An id is unique within
  its own list only.
- Field names are camelCase. Nullable fields are present with `null`, never omitted.

### Top level

| Field | Type | Meaning |
|---|---|---|
| `format` | `"ledger-export"` | Identifies the file |
| `formatVersion` | `3` | Incremented only for incompatible changes. Readers should refuse higher versions. New fields may be added within a version. Version 2 (Ledger 2.4) added the transaction type `refund`; version 3 (2.4.1) added `outsideIn` and `outsideOut`. Nothing else changed from version 1. |
| `exportedAt` | timestamp | |
| `appVersion` | string | Ledger version that wrote the file |
| `currency` | `"USD"` | All amounts are in this currency |
| `accounts`, `categories`, `transactions`, `budgets`, `recurring`, `recurringSkips`, `goals`, `goalContributions`, `jobs`, `shifts`, `paycheckRecords`, `jobDeductions`, `paystubs`, `importBatches`, `importProfiles`, `importPayeeRules`, `settings` | arrays | Below |

### Sections

- **accounts**: `id, name, type (checking|savings|cash|creditCard|other), institutionName,
  startingBalanceCents, balanceDate, isIncludedInNetWorth, isArchived, sortOrder, createdAt, updatedAt`.
  - `balanceDate` (added in 2.4.2): the day `startingBalanceCents` is the bank's balance for.
    Transactions on or before it are already inside it; `null` means before every transaction.
  - A credit card's `startingBalanceCents` is stored negative when money is owed.
  - Balances are not exported, because they are always derived: starting balance plus
    transactions after `balanceDate` (all of them when it's `null`).
- **categories**: `id, name, parentCategoryId, type (expense|income|savings), icon, isArchived, sortOrder`.
  - There are at most two levels, and a subcategory shares its parent's type.
- **transactions**: `id, date, type (income|expense|transfer|refund|outsideIn|outsideOut), amountCents, accountId,
  transferAccountId, categoryId, payee, memo, status (pending|cleared|reconciled),
  recurringTransactionId, recurrenceDate, isAutoRecorded, externalId, importBatchId, reconciliationId, paidFromGoalId,
  isDeleted, deletedAt, createdAt, updatedAt`.
  - `reconciliationId` (added in 2.6) names the statement a `reconciled` transaction was matched with.
  - `paidFromGoalId` (added in 2.9.9) is the sinking fund a spending transaction was paid from. It's
    still spending in its category.
  - A transfer is one record: money leaves `accountId` and arrives in `transferAccountId`.
  - A refund is money into `accountId` with a spending category. Subtract it from spending in
    its category and month; it is never income.
  - `outsideIn` / `outsideOut` is a transfer with someone else's account (e.g. a family member's):
    money into or out of `accountId`, with no `transferAccountId`. It is neither income nor
    spending, but it does change the balance and net worth.
  - Deleted transactions are included with `isDeleted: true`, so the export is complete. Ignore
    them for totals.
  - `externalId` is the bank's id for an imported transaction (QFX FITID, or a CSV reference
    column); `importBatchId` names the import that added it. Both are `null` for hand-entered ones.
- **budgets**: `id, year, month, items[] { categoryId, plannedAmountCents }`.
  - Spending against a budget is never stored; compute it from transactions.
- **recurring**: `id, name, type, amountCents, isAmountEstimated, frequency
  (weekly|biweekly|monthly|quarterly|yearly), startDate, endDate, accountId, transferAccountId,
  categoryId, payee, memo, isActive, autoRecordSince, createdAt, updatedAt`.
  - Occurrence n falls on `startDate` + n periods. Monthly dates past the end of a month use its
    last day.
- **recurringSkips**: `recurringTransactionId, date`. These are occurrences the user skipped.
- **goals**: `id, name, targetAmountCents, startingAmountCents, targetDate, tracking
  (manual|accountBalance), accountId, isSinkingFund, monthlySetAsideCents, categoryId, isArchived,
  sortOrder, createdAt, updatedAt`.
  - Progress for a manual goal is `startingAmountCents` + the sum of its contributions, minus the
    amounts of (non-deleted) transactions whose `paidFromGoalId` is this goal.
  - A sinking fund (`isSinkingFund`, added in 2.9.9) is money set aside for a known cost; it's always
    manual, may set aside `monthlySetAsideCents` a month, and pays for spending in `categoryId`.
  - Progress for an account-backed goal is that account's balance.
- **goalContributions**: `id, goalId, date, amountCents (signed: + added, − taken out), note, createdAt`.
- **jobs**: `id, name, payFrequency, payPeriodAnchor, paydayOffsetDays, isPaydayConfirmed,
  workweekStart, isOvertimeEligible, unpaidBreakMinutes, unpaidBreakAfterMinutes,
  paidBreakMinutes, depositAccountId, incomeCategoryId, filingStatus, w4MultipleJobs,
  w4DependentsCreditCents, w4OtherIncomeCents, w4DeductionsCents, w4ExtraWithholdingCents,
  workLocationName, workPsdCode, workNonResidentEitBasisPoints, workLstAnnualCents, isLstExempt,
  isArchived, rates[] { effectiveDate, hourlyRateCents }`.
  - Basis points: `50` = 0.50%.
- **shifts**: `id, jobId, date, scheduledMinutes, startTime, endTime, breakOverrideMinutes,
  status (scheduled|worked), note`.
  - Paid minutes are derived from the job's break rule; see `docs/RULES.md`.
- **paycheckRecords**: `jobId, periodStart, payDate, estimatedNetCents, transactionId`. Each one
  links a real paycheck to the estimate it paid.
- **jobDeductions** (added in 2.0): `id, jobId, name, kind (preTaxRetirement|preTaxHealth|afterTax),
  amountCents, percentBasisPoints, startDate, endDate`.
  - A deduction is either a fixed amount per paycheck, or `percentBasisPoints` of gross (`500` = 5%).
  - It applies to pay periods ending from `startDate` through `endDate`.
- **paystubs** (added in 2.0): `jobId, periodStart, stubPeriodStart, stubPeriodEnd, payDate, paidMinutes,
  grossCents, federalCents, socialSecurityCents, medicareCents, stateCents, stateUnemploymentCents,
  localEitCents, localServicesTaxCents, netCents`. These are the sample paystub numbers; any
  line may be `null` (not entered).
- **importBatches** (added in 2.3): `id, accountId, fileName, importedAt, transactionCount, isUndone`.
  One per bank file imported. An undone import's transactions are deleted (`isDeleted: true`).
- **importProfiles** (added in 2.3): `accountId, bankAccountKey, dateColumn, descriptionColumn,
  amountColumn, debitColumn, creditColumn, memoColumn, referenceColumn, dateFormat, invertSign`.
  How to read each account's bank file, remembered after the first import.
- **importPayeeRules** (added in 2.3): `key, payee, categoryId, transferAccountId, isOutsideTransfer`. A cleaned bank
  description (upper case) and the payee and category the user chose for it, or for a transfer
  between their own accounts, the other account (`transferAccountId`, added in 2.3.2).
- **reconciliations** (added in 2.6): `id, accountId, statementDate, statementBalanceCents, reconciledAt,
  transactionCount, isUndone`. Each statement matched against Ledger. `statementBalanceCents` is signed
  like account balances (a card's amount owed is negative); an undone one's transactions are no longer
  reconciled.
- **settings**: `key, value`. These are app preferences (theme, home tax location, …). Saved what-if
  scenarios are the `Planning.Scenarios` setting (JSON: each scenario's name and assumptions); they are
  never transactions, bills or goals.

Predictions (paychecks, forecasts, suggested goal amounts) are not stored, so they are not
exported. They are recomputed from the data above, using the rules in `docs/RULES.md`.

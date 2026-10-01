# Ledger

A calm, local-first personal finance app for Windows. Built with WinUI 3, the Windows App SDK, .NET 10, SQLite and EF Core.

Your data lives only on this PC, in `%LOCALAPPDATA%\Ledger` (or, for the portable copy, a `Data` folder beside `Ledger.exe`).

**Status: 3.0.0.** V1 (1.9.0) and V2 (2.9.9) are complete; 3.0.0 adds a welcome tour, built-in Help and a shareable Windows installer. See the [changelog](CHANGELOG.md).

## What it does

- **Overview:** money available, this month's income, spending and saving, the next two weeks, budget and goals.
- **Transactions:** fast entry, search and filters, transfers that never count as spending, and undo.
- **Budget:** monthly plans per spending group vs actual spending.
- **Income:** paycheck predictions from your shifts, with federal, FICA, Pennsylvania and local taxes and automatic breaks.
- **Bills:** recurring bills and income, a day-by-day balance forecast, and optional automatic recording.
- **Goals:** savings goals with progress and a suggested monthly amount.
- **Reports:** spending by category, cash flow and net worth.
- **Import and reconcile:** bank CSV/QFX import with duplicate detection, and statement reconciliation.
- **Sinking funds and What if?:** money set aside for known costs, "Can I afford this?" and scenarios.
- **Data:** backups (automatic and manual), restore, and CSV and JSON export. No account, no cloud.
- **Help:** a welcome tour and searchable Help (F1 on any page).

## Layout

```
src/Ledger.Core/        Models, EF Core context + migrations, financial services (no UI dependencies)
src/Ledger.App/         WinUI 3 app: Views, ViewModels, theme resources, navigation
tests/Ledger.Core.Tests xUnit tests against a real (in-memory) SQLite database using the production migrations
```

Financial logic lives only in `Ledger.Core/Services`. View models call services and never calculate money themselves.

## Build, run, test

```bash
dotnet build Ledger.slnx
dotnet run --project src/Ledger.App
dotnet test Ledger.slnx
```

UI smoke test (debug build, throwaway database, never your real data):

```bash
powershell -ExecutionPolicy Bypass -File scripts/smoke-test.ps1
```

To install for everyday use (Release build in `%LOCALAPPDATA%\Programs\Ledger` plus a desktop shortcut; re-run to update, add `-StartMenu` for a Start menu entry):

```bash
powershell -ExecutionPolicy Bypass -File scripts/install.ps1
```

To build the shareable release (a one-file installer and a portable zip in `dist\Windows-v<version>`):

```bash
powershell -ExecutionPolicy Bypass -File scripts/package.ps1
```

The installer (`installer/Setup.cs`) is compiled with the .NET SDK's C# compiler against the .NET Framework built into Windows, so it runs anywhere without prerequisites. It installs per user, never needs administrator rights, and never touches anyone's data.

No Visual Studio is required. The app is unpackaged and bundles the Windows App SDK runtime (x64 by default; pass `-p:Platform=ARM64` for ARM).

## Migrations

```bash
dotnet tool restore
dotnet ef migrations add <Name> --project src/Ledger.Core --output-dir Data/Migrations
```

Migrations are applied automatically at startup. Never edit the schema any other way.

## Data model rules

- Money is stored as `long` cents. `Money` is the only place amounts are parsed or formatted.
- Transaction amounts are positive. `TransactionType` gives the direction.
- A transfer is one row, from `AccountId` to `TransferAccountId`. Transfers are never income or spending, and never change net worth. Paying a credit card is a transfer.
- Balances are always derived: starting balance plus non-deleted transactions dated on or before today. No stored "current balance".
- Transactions are soft-deleted (`IsDeleted`), and a global query filter hides them.
- Recurring rules compute their occurrences from `StartDate`. No mutable "next occurrence" is stored. A posted occurrence is unique per `(RecurringTransactionId, RecurrenceDate)`, so posting is idempotent.
- Foreign keys use `RESTRICT`, so deleting something never cascades into history.

## Docs

- [V1 completion record](docs/V1.md): scope, definition of done, what's next, yearly upkeep.
- [Portability rules](docs/PORTABILITY.md): how the code and data stay portable to a future Swift/SwiftUI app.
- [Export format](docs/EXPORT_FORMAT.md): the CSV and JSON exports and backup files, field by field.
- [Rules](docs/RULES.md): every rule behind the numbers, in plain language, with the tests that enforce it.
- [V2 scope](docs/plans/v2.md): proposed V2 features, order, design and open decisions.
- [Income Calculator plan](docs/plans/income-calculator.md): plan and 2026 Pennsylvania payroll tax research.
- [Changelog](CHANGELOG.md)

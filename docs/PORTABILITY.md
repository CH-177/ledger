# Portability rules

Ledger is a Windows app, but it is built so a future Swift/SwiftUI version (iPhone, iPad, Mac)
can reuse its logic and its data. These rules apply to all new work.

1. **Financial rules live in `Ledger.Core`, never in view models or views.** Core has no UI or
   Windows dependencies, so it can be ported line for line. View models only format and forward.
2. **The schema stays plain SQLite.** Money is integer cents, dates are `yyyy-MM-dd` text,
   timestamps are ISO text, and enums are small integers that are never renumbered. Avoid EF-only
   features that another SQLite client couldn't reproduce with its own migrations.
3. **Exports are a documented, versioned format.** The JSON export (Phase 10) carries a format
   version and is documented in `docs/EXPORT_FORMAT.md`, so another app can import it.
4. **Business rules are written down.** `docs/RULES.md` states each rule in plain language
   (transfers are never spending, balances are derived, …) and points to the tests that enforce
   it. Those tests are the specification a port has to pass.
5. **Reference data is data, not code.** Things like yearly tax tables live in versioned JSON
   files under `src/Ledger.Core/Data/Reference/`, so any platform can load the same file.

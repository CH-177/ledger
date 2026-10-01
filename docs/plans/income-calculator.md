# Plan: Income Calculator

**Status:** Core built in **1.3.0** (2026-09-27). Covered: jobs, rates, pay schedule, shifts,
pasting a schedule, gross and net paycheck estimates, totals, and recording real paychecks.

Example setup used during development:
- Riverside Market, $13.50/hour, weekly pay.
- Works in **Riverton City** (PSD 990201: 0.5% non-resident rate, $52 LST, 2025 list).
- Single, no W-4 boxes checked.
- The payday is *assumed* to be the Friday after a Sunday–Saturday week, until confirmed.

Still to do (next minor version):
- Paycheck deductions UI (401(k), health). The calculator already supports them.
- A paystub comparison check.
- A self-employed set-aside, if the photography becomes a business.
- Feeding predicted paychecks into the forecast (Phase 7).
**Goal:** Enter your job(s), how and when you're paid, and the shifts your employer schedules.
Ledger then predicts each paycheck (gross, every deduction, and net) and totals your expected
income per week, per two weeks, and per month.

Predicted pay is always labelled **Estimate**. It is never recorded as money you have, which
follows the brief's rule that forecasts never pretend to be real. It feeds the forecast
(Phase 7 onward) as expected income.

---

## 1. What you'll be able to do

1. **Add a job:** employer name, hourly rate (or salary), pay frequency, a recent pay-period
   start date, and how many days after the period ends payday lands. Also where you work (for
   local tax), your W-4 choices, and any paycheck deductions (401(k), health insurance).
2. **Paste your schedule:** copy the shifts your employer's app or site shows you and paste them
   into Ledger. It reads the dates and times, shows a preview to confirm, and saves them.
   Re-pasting the same week updates it instead of duplicating. Manual entry, and "repeat last
   week", are there too.
3. **See predictions:**
   - Upcoming paydays, each with estimated gross and net pay.
   - Totals for this week, the next two weeks, and this month. You can view these by
     **payday** (when the money arrives; best for planning) or by **work date** (when you
     earned it).
   - A paystub-style breakdown of each paycheck: hours, overtime, gross, each tax, net.
4. **Check against a real paystub (recommended once per job):** type the numbers from one real
   paystub. Ledger compares them line by line with its own calculation and helps you correct any
   setting that differs, so later predictions match your employer's payroll.
5. **Record a real paycheck:** "Record this paycheck" opens the transaction dialog prefilled
   with the estimated net pay. You enter the actual amount. The estimate is never overwritten
   automatically, and the pair is kept so Ledger can show how accurate its estimates are.
6. **Self-employed income (e.g. photography):** enter expected gigs. Ledger shows a suggested
   amount to **set aside for taxes**, because nobody withholds tax from this income for you.

---

## 2. How take-home pay is calculated (2026, living in Maple Boro, PA)

All rates below were checked against official sources (listed in §8) unless marked **verify**.
Rates are stored as yearly reference data, not hard-coded (see `docs/PORTABILITY.md`).

### 2.1 Gross pay

- **Hours per shift** = end time − start time − unpaid break. Shifts that cross midnight are
  handled, and durations use your local time zone, so daylight-saving nights are counted correctly.
- **Overtime:** federal law and the PA Minimum Wage Act both require **1.5×** pay for hours over
  **40 in a workweek**. The workweek is a fixed 7-day period set by the employer (the job has a
  "workweek starts on" setting). Overtime is decided per workweek, not per pay period.
- **Gross** = regular hours × rate + overtime hours × rate × 1.5, plus any extra pay entered for
  that period (bonus, holiday premium).
- PA minimum wage is still **$7.25/hour** in 2026. The app warns if a rate is below it.
- **Pay periods** repeat from the anchor date you give: weekly (52 a year), every two weeks (26,
  with an occasional 27-period year), twice a month (24), or monthly (12).
  **Payday** = period end + the offset you give.

### 2.2 Deductions and taxes on each paycheck (W-2 job)

Each line is rounded to the cent, then subtracted from gross.

| Line | Rate (2026) | Taxed on | Notes |
|---|---|---|---|
| **Federal income tax withholding** | Brackets from IRS Pub 15-T 2026 | Gross − pre-tax 401(k) − Section 125 health | Annualized percentage method (Worksheet 1A): wages × pay periods, − $8,600 single / $12,900 married filing jointly (only if W-4 Step 2 is *not* checked), − Step 4(b) deductions, + Step 4(a) other income. Then the rate table, − Step 3 credits, ÷ pay periods, + Step 4(c) extra. Uses the Step 2 table if "multiple jobs" is checked. |
| **Social Security** | 6.2% | Gross − Section 125 (401(k) is **not** excluded) | Stops once year-to-date wages *at that employer* reach **$184,500**. |
| **Medicare** | 1.45%, + 0.9% on wages over $200,000 | Same as Social Security | No cap. The extra 0.9% is withheld above $200k per employer. |
| **PA personal income tax** | **3.07%** flat | Gross − Section 125 only | PA taxes 401(k) contributions even though federal tax does not. |
| **PA unemployment (employee share)** | **0.07%** | Gross | No wage cap. |
| **Local earned income tax (EIT)** | **Higher of** your home rate or your work location's non-resident rate (Act 32) | Same base as PA income tax (**verify** the 401(k) and Section 125 treatment for local EIT) | Home: **Maple Boro, PSD 990101, Maple Area SD, 1.5% resident** (2025 statewide list; **verify** 2026). |
| **Local Services Tax (LST)** | Set by the **work** location, max **$52/year** | Flat amount | Over $10: split evenly across the year's pay periods (rounded down to the cent). $10 or less: may be taken as a lump sum. Only one employer collects it per pay period; $52 cap per year. Towns with LST over $10 must exempt people earning under $12,000 there (optional toggle). Maple Boro's own LST is $10, but what applies is **where you work**. |
| **Pre-tax deductions** | As entered | n/a | 401(k): reduces federal tax only. Section 125 health, dental, vision: reduces federal, Social Security, Medicare and PA tax. |
| **After-tax deductions** | As entered | n/a | e.g. Roth 401(k), union dues. |

**Net pay** = gross − all of the above.

**What this predicts:** the paycheck, i.e. what your employer *withholds*. That isn't the same
as the tax you'll finally owe. Refunds or balances due at filing are out of scope for the first
version. Two 2026 federal deductions, for **overtime** (up to $12,500) and **tips** (up to
$25,000), lower your *annual* tax but **do not reduce paycheck withholding** unless you claim
them on a new W-4 (Step 4(b)). The app says so next to overtime pay.

### 2.3 Worked example

Hourly $15, 60 hours in a two-week period (no overtime), single, W-4 Step 2 not checked, no
deductions, work-location EIT ≤ 1.5%, no LST due this check:

| Line | Calculation | Amount |
|---|---|---|
| Gross | 60 × $15 | **$900.00** |
| Federal | ($900 × 26 − $8,600) = $14,800 → 10% × ($14,800 − $7,500) = $730/year ÷ 26 | −$28.08 |
| Social Security | 6.2% | −$55.80 |
| Medicare | 1.45% | −$13.05 |
| PA income tax | 3.07% | −$27.63 |
| PA unemployment | 0.07% | −$0.63 |
| Local EIT | 1.5% | −$13.50 |
| **Estimated net** | | **$761.31** (about 84.6% of gross) |

This example becomes one of the automated tests.

### 2.4 Self-employed income (photography)

Nothing is withheld, so the app suggests a **set-aside** percentage for each gig. It is never
treated as money already paid:
- Self-employment tax: 15.3% (12.4% Social Security + 2.9% Medicare) on 92.35% of net profit.
  The Social Security part shares the $184,500 cap with W-2 wages.
- Federal income tax at your estimated marginal rate.
- PA 3.07% and local EIT 1.5% on net profit.
- A reminder that quarterly estimated payments are generally required once you expect to owe
  **$1,000 or more** for the year. The app can list the 1040-ES due dates. (A tax filing feature
  is explicitly excluded from V1.)

---

## 3. Pasting a schedule

Employer apps show schedules differently, so the parser is tolerant and always shows a **preview
before saving**. Lines it can't read are listed, never silently dropped.

- **Dates:** `9/28`, `9/28/2026`, `Sep 28`, `September 28`, `Mon 9/28`, or a bare weekday name
  plus a "week of" date you pick.
- **Times:** `4:00 PM - 9:30 PM`, `4p-9:30p`, `16:00–21:30`, and overnight shifts like
  `10:00 PM - 6:00 AM`.
- **Breaks:** `(30 min meal)`, `Meal: 0:30`. Otherwise it uses the job's default unpaid break.
- **Days off:** words like `OFF`, `Unavailable` or `Time off` are recognised and skipped.
- **Duplicates:** matched by job + date + start time. A re-paste replaces that week's
  *scheduled* shifts, after confirming.
- **Scheduled vs worked:** shifts start as *Scheduled*. After the day passes you can mark one
  *Worked* and adjust the times. Past periods use worked hours when available.

**Needed from you:** a sample of your schedule text exactly as it pastes (names removed if you
like). The parser will be built and tested against it.

---

## 4. Data model (new migration)

- `Job`:
  - Name, pay type (Hourly/Salary), tax treatment (W-2/Self-employed), pay frequency.
  - Pay-period anchor date, payday offset in days, workweek start day, overtime on/off.
  - Default unpaid break, deposit account, income category.
  - Work location: PSD code, non-resident EIT rate, LST amount.
  - W-4: filing status, Step 2 checkbox, Steps 3/4(a)/4(b)/4(c).
  - IsArchived.
- `JobRate`: an effective-dated hourly rate or salary, so raises don't rewrite past estimates.
- `Shift`: job, date, start time, end time, unpaid break minutes, status (Scheduled/Worked),
  source (Manual/Pasted), note.
- `PayrollDeduction`: job, name, kind (pre-tax 401(k) / pre-tax Section 125 / after-tax), fixed
  amount or % of gross.
- `PayAdjustment`: job, pay-period start, amount, note (bonus, holiday premium).
- `PaycheckRecord`: job, pay date, estimated net, and a link to the actual income transaction.
  Used for accuracy tracking.
- **Settings:** home PSD code, home resident EIT rate (default 1.5%), default filing status.
- **Reference data:** `Data/Reference/tax-2026.json` holds the federal tables, Social Security
  base, rates, and PA and UC rates. A new year means a new file.

Amounts are integer cents. Rates are stored as basis points (1.5% = 150). Hours are stored as
minutes, so there's no rounding drift.

## 5. Core services (all pure calculation, fully tested)

- `ScheduleParser`: text → proposed shifts + unreadable lines.
- `PayPeriodCalendar`: anchor + frequency → periods and paydays.
- `GrossPayCalculator`: shifts → per-workweek regular and overtime hours → per-period gross.
- `PayrollTaxCalculator`: gross + job settings + year-to-date → an itemised paycheck, using the
  tax reference file.
- `IncomeForecastService`: combines the above into predicted paychecks, and totals per week,
  two weeks and month (by payday or by work date). Phase 7's `ForecastService` consumes it.

## 6. Screens

- **Income** gets a new item in the navigation rail, after Budget:
  - Next paydays, with an estimated net for each.
  - Week / Two weeks / Month totals, with a By payday / By work date switch.
  - Each value is labelled "Estimate".
- **Jobs:** a guided add/edit covering pay basics → pay schedule → where you work → W-4 →
  deductions, then an optional paystub check.
- **Schedule:** a week list of shifts, with Paste schedule, Add shift and Repeat last week.
- **Paycheck detail:** the paystub-style breakdown and "Record this paycheck".
- **Settings → Taxes:** home location and default filing status.

## 7. Delivery (proposed; what shipped is noted below)

As built: 1.3.0 shipped gross **and** net predictions and record-this-paycheck together; 1.3.1 added
automatic breaks; 1.4.0 is Phase 7, whose forecast already counts predicted paychecks as expected
income. Deductions, the paystub check and the self-employed set-aside are still to come.

| Version | Scope |
|---|---|
| 1.3.0 | Jobs, rates, pay schedule, shifts (manual + paste), **gross** predictions and paydays, Income page. |
| 1.4.0 | Taxes and deductions → **net** predictions, paycheck detail, paystub check, record-this-paycheck, self-employed set-aside. |
| 1.5.0 | Phase 7 (recurring bills); the forecast uses predicted paychecks as expected income. |

**Tests to include:**
- Pub 15-T worked examples.
- The §2.3 example.
- Overtime across workweek boundaries, and a workweek split between pay periods.
- Overnight and daylight-saving shifts.
- Biweekly years with 27 pay periods, and semimonthly periods.
- The Social Security cap crossed partway through a paycheck.
- The Act 32 higher-of rule.
- LST lump sum vs pro-rata, the $52 cap, and the low-income exemption.
- 401(k) taxed by PA but not federal; Section 125 exempt from both.
- Parser samples, including your real one.

## 8. Sources

- IRS Publication 15-T (2026), federal withholding tables and Worksheet 1A: https://www.irs.gov/publications/p15t
- Social Security 2026 wage base $184,500: https://www.patriotsoftware.com/blog/payroll/social-security-wage-base/
- Medicare and the additional 0.9%: https://www.irs.gov/taxtopics/tc751, https://www.irs.gov/taxtopics/tc560
- PA income tax 3.07%, and 401(k) / Section 125 treatment: https://www.pa.gov/agencies/revenue/forms-and-publications/pa-personal-income-tax-guide/gross-compensation
- PA UC employee rate 0.07% (2026): https://www.pa.gov/agencies/dli/resources/for-employers-and-educators/how-to-file/uc-tax/calculating-contributions--penalties-and-interest
- Act 32 higher-of rule: https://dced.pa.gov/local-government/local-income-tax-information/act32-faq/
- Local Services Tax rules: https://dced.pa.gov/local-government/local-income-tax-information/local-services-tax/
- Maple Boro PSD 990101, 1.5% resident, $10 LST: 2025 statewide PSD Code List (berkseit.com); official lookup at https://munstats.pa.gov/public/findlocaltax.aspx
- Overtime and minimum wage: https://www.pa.gov/agencies/dli/resources/compliance-laws-and-regulations/labor-management-relations/labor-law/overtime-and-tipped-worker-rules-in-pa
- Self-employment tax and estimated payments: https://www.irs.gov/businesses/small-businesses-self-employed/self-employment-tax-social-security-and-medicare-taxes, https://www.irs.gov/faqs/estimated-tax/individuals

## 9. Open questions for you

1. **A sample of your schedule text**, exactly as it pastes from your employer's app.
2. **Where do you work** (town)? That sets the local tax rate and the LST.
3. For each job: rate, pay frequency, a recent pay-period start date, and your payday.
4. Filing status, whether you checked W-4 Step 2 ("multiple jobs"), and any 401(k) or health
   deductions.
5. Is photography income self-employed (no withholding)?
6. Do the Income Calculator first (1.3.0/1.4.0) or Phase 7 recurring bills first?

It's a judgement call, not required: the most accurate setup is to run the paystub check once
per job. The paystub numbers stay on this PC.

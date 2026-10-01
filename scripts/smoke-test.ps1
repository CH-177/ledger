<#
.SYNOPSIS
    Clicks through Ledger's UI on a throwaway database and fails if anything breaks.

.DESCRIPTION
    Builds (unless -NoBuild) and starts the DEBUG build with LEDGER_DATA_DIR pointing at a new temp
    folder, so it never sees your real data (%LOCALAPPDATA%\Ledger). It then walks first launch and
    the welcome tour, opens every page (and Import, Reconcile and a Help search), adds a transaction,
    a bill and a goal, asks "Can I afford this?", checks the app is still running, and closes it
    normally. Only the debug build it started is ever touched.

    Run it after UI changes:  powershell -ExecutionPolicy Bypass -File scripts\smoke-test.ps1
#>
param([switch]$NoBuild, [switch]$KeepData)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $repo 'src\Ledger.App\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Ledger.exe'

if (-not $NoBuild) {
    dotnet build (Join-Path $repo 'src\Ledger.App') -c Debug -p:Platform=x64 -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}
if (Get-Process Ledger -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }) {
    throw 'A debug Ledger is already running. Close it first.'
}

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$A = [System.Windows.Automation.AutomationElement]
$T = [System.Windows.Automation.TreeScope]
$data = Join-Path ([IO.Path]::GetTempPath()) ("ledger-smoke-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$failures = New-Object System.Collections.Generic.List[string]

function Get-App { Get-Process Ledger -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Select-Object -First 1 }
function Get-Roots {
    $p = Get-App; if (-not $p) { throw 'Ledger stopped running.' }
    $A::RootElement.FindAll($T::Children, (New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $p.Id)))
}
function Find([string]$name, [string]$type) {
    for ($i = 0; $i -lt 20; $i++) {
        foreach ($r in Get-Roots) {
            foreach ($e in $r.FindAll($T::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $name)))) {
                if ($e.Current.ControlType.ProgrammaticName -eq "ControlType.$type") { return $e }
            }
        }
        Start-Sleep -Milliseconds 250
    }
    throw "Not found: $name ($type)"
}
function Click([string]$name, [string]$type = 'Button') { (Find $name $type).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 800 }
function Pick([string]$name) { (Find $name 'ListItem').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 1200 }
function Type-Into([string]$name, [string]$value) { (Find $name 'Edit').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value); Start-Sleep -Milliseconds 200 }
function Has-Text([string]$text) {
    foreach ($r in Get-Roots) {
        if ($r.FindFirst($T::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $text)))) { return $true }
    }
    $false
}
function Step([string]$label, [scriptblock]$action) {
    try { & $action; Write-Host "  ok    $label" -ForegroundColor Green }
    catch { $failures.Add("$label : $($_.Exception.Message)"); Write-Host "  FAIL  $label : $($_.Exception.Message)" -ForegroundColor Red }
}

Write-Host "Smoke test on a throwaway database: $data"
$env:LEDGER_DATA_DIR = $data
Start-Process $exe
Start-Sleep 8
try {
    Step 'First launch: welcome' { Click 'Continue' }
    Step 'First launch: create account' { Type-Into 'Account name' 'Checking'; Type-Into 'Starting balance' '500'; Click 'Create Account' }
    Step 'First launch: take the tour' { Click 'Take the tour'; if (-not (Has-Text 'Overview: where you stand')) { throw 'tour did not start' } }
    Step 'Tour: every step, then Done' {
        for ($i = 1; $i -lt 10; $i++) { Click 'Next' }
        if (-not (Has-Text 'Help is always here')) { throw 'tour did not reach Help' }
        Click 'Done'
        if (Has-Text 'Help is always here') { throw 'tour did not close' }
    }

    foreach ($page in 'Transactions', 'Budget', 'Income', 'Bills', 'Goals', 'What if?', 'Accounts', 'Reports', 'Help', 'Settings', 'Overview') {
        Step "Open $page" { Pick $page }
    }

    Step 'Help: search finds backups' {
        Pick 'Help'; Type-Into 'Search help' 'backup'
        if (-not (Has-Text 'Backups and restore')) { throw 'no Backups topic' }
    }
    Step 'Import page opens' { Pick 'Transactions'; Click 'Import from your bank'; if (-not (Has-Text 'Import from your bank')) { throw 'Import page did not open' } }
    Step 'Reconcile page opens' {
        Pick 'Accounts'; Start-Sleep 1; Click 'More options for Checking'; Start-Sleep 1
        (Find ('Reconcile with a statement' + [char]0x2026) 'MenuItem').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 1
        if (-not (Has-Text 'Reconcile with a statement')) { throw 'Reconcile page did not open' }
    }

    Step 'Add a transaction' {
        Pick 'Transactions'; Click 'Add transaction'
        Type-Into 'Amount' '8.42'; Type-Into 'Payee' 'Smoke test'; Click 'Save'
        if (-not (Has-Text 'Smoke test')) { throw 'transaction not listed' }
    }
    Step 'Add a bill' {
        Pick 'Bills'; Click 'Add recurring'
        Type-Into 'Name' 'Smoke bill'; Type-Into 'Amount' '12.00'; Click 'Save'
        if (-not (Has-Text 'Smoke bill')) { throw 'bill not listed' }
    }
    Step 'Add a goal' {
        Pick 'Goals'; Click 'Add goal'
        Type-Into 'Name' 'Smoke goal'; Type-Into 'Target amount' '100'; Click 'Save'
        if (-not (Has-Text 'Smoke goal')) { throw 'goal not listed' }
    }
    # Today's transaction is inside the new account's balance (its balance date is today), so
    # Available stays $500.00 while this month's spending shows it.
    Step 'Dashboard reflects the transaction' {
        Pick 'Overview'
        if (-not (Has-Text '$500.00')) { throw 'Available is not $500.00' }
        if (-not (Has-Text '-$8.42')) { throw 'spending does not show -$8.42' }
    }
    Step 'What if: can I afford this' {
        Pick 'What if?'; Type-Into 'Amount' '50'; Click 'Check'
        if (-not ((Get-Roots | ForEach-Object { $_.FindAll($T::Descendants, [System.Windows.Automation.Condition]::TrueCondition) } |
                   Where-Object { $_.Current.Name -like 'Yes.*' -or $_.Current.Name -like 'Not *' -or $_.Current.Name -like 'Only *' }))) { throw 'no answer' }
    }
    Step 'Still running' { if (-not (Get-App)) { throw 'Ledger exited' } }
}
finally {
    if ($p = Get-App) {
        $w = Get-Roots | Select-Object -First 1
        try { $w.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() } catch { }
        Start-Sleep 3
        if ($p = Get-App) { $p | Stop-Process -Force }
    }
    Remove-Item Env:\LEDGER_DATA_DIR -ErrorAction SilentlyContinue
    if (-not $KeepData) { Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue }
}

if ($failures.Count -gt 0) {
    Write-Host "`n$($failures.Count) step(s) failed." -ForegroundColor Red
    exit 1
}
Write-Host "`nAll steps passed." -ForegroundColor Green

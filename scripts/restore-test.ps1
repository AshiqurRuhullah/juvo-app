<#
.SYNOPSIS
  Proves a backup can be restored: restores the latest backup into a throw-away database, checks it, prints PASS or FAIL, drops it.

.DESCRIPTION
  1. Finds the latest backup file in the folder (juvo-backup_<db>_*.bak from the application or juvo-manual_<db>_*.bak from backup-db.ps1; newest by file time) or uses -BackupFile.
  2. Restores it into  JuvoDb_RestoreTest_<yyyyMMddHHmmss>  (scripts\restore-db.ps1; the real database is never touched).
  3. Checks:
       - DBCC CHECKDB (no errors)
       - the key tables exist and their row counts are readable (printed)
       - the last row of __EFMigrationsHistory equals the newest migration in the code (the Migrations folder)
       - sample order total check: for up to 50 newest orders, GrandTotal = Subtotal - PromotionDiscount - CouponDiscount - ManualDiscount + DeliveryCharge
         (that is how the application fixes the total when an order is placed); "no orders" is reported as skipped, not as a pass.
  4. Prints a report ending in PASS or FAIL, drops the throw-away database (unless -KeepDatabase) and exits 0 (PASS) or 1 (FAIL).

  Schedule it (Task Scheduler, weekly) and look at the result: a backup that was never restored is only a hope.

.PARAMETER Database
  The source database name used to find the latest backup. Default JuvoDb.

.PARAMETER Folder
  Where the backups are. Default: juvo-be\src\Juvo.Api\App_Data\backups when it exists, else .run\backups.

.PARAMETER BackupFile
  Test this file instead of the latest one in the folder.

.PARAMETER Server
  SQL Server instance. Default (localdb)\MSSQLLocalDB.

.PARAMETER MigrationsPath
  Folder with the code's migrations. Default juvo-be\src\Juvo.Infrastructure\Persistence\Migrations.

.PARAMETER KeepDatabase
  Do not drop the throw-away database (to look at it).

.EXAMPLE
  .\scripts\restore-test.ps1
  .\scripts\restore-test.ps1 -Folder "D:\JuvoBackups" -Server ".\SQLEXPRESS"
#>
[CmdletBinding()]
param(
    [string]$Database = "JuvoDb",
    [string]$Folder,
    [string]$BackupFile,
    [string]$Server = "(localdb)\MSSQLLocalDB",
    [string]$MigrationsPath,
    [switch]$KeepDatabase
)

$ErrorActionPreference = "Stop"

if ($Database -notmatch '^[A-Za-z_][A-Za-z0-9_]{0,63}$') { throw "Invalid database name '$Database'." }
if (-not (Get-Command sqlcmd -ErrorAction SilentlyContinue)) { throw "sqlcmd was not found on PATH (winget install Microsoft.Sqlcmd)." }

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $MigrationsPath) { $MigrationsPath = Join-Path $repoRoot "juvo-be\src\Juvo.Infrastructure\Persistence\Migrations" }
if (-not $Folder) {
    $apiFolder = Join-Path $repoRoot "juvo-be\src\Juvo.Api\App_Data\backups"
    $Folder = if (Test-Path -LiteralPath $apiFolder) { $apiFolder } else { Join-Path $repoRoot ".run\backups" }
}

function Invoke-Sql([string]$Query, [string]$Db = 'master') {
    $sqlArgs = @('-S', $Server, '-E', '-C', '-b', '-d', $Db, '-h', '-1', '-W', '-s', '|', '-t', 0, '-Q', "SET NOCOUNT ON; $Query")
    $out = & sqlcmd @sqlArgs 2>&1
    if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed: $(($out | Out-String).Trim())" }
    return ($out | Out-String).Trim()
}

$results = New-Object System.Collections.Generic.List[object]
function Add-Result([string]$Name, [string]$State, [string]$Detail) {
    $results.Add([pscustomobject]@{ Check = $Name; Result = $State; Detail = $Detail })
}

# ---- 1. the backup file ----------------------------------------------------------------------------
if (-not $BackupFile) {
    if (-not (Test-Path -LiteralPath $Folder)) { Write-Host "FAIL: backup folder not found: $Folder" -ForegroundColor Red; exit 1 }
    $candidate = Get-ChildItem -LiteralPath $Folder -File |
        Where-Object { $_.Name -match "^juvo-(backup|manual)_${Database}_\d{8}T\d{6}Z_[0-9a-f]{8}\.bak$" } |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if (-not $candidate) { Write-Host "FAIL: no backup of '$Database' found in $Folder" -ForegroundColor Red; exit 1 }
    $BackupFile = $candidate.FullName
}
$ageHours = [math]::Round(([DateTime]::UtcNow - (Get-Item -LiteralPath $BackupFile).LastWriteTimeUtc).TotalHours, 1)
Write-Host "Backup    : $BackupFile  (age $ageHours h)"

$test = "JuvoDb_RestoreTest_$([DateTime]::Now.ToString('yyyyMMddHHmmss'))"
Write-Host "Throw-away: $test on $Server"
Write-Host ""

$restored = $false
try {
    # ---- 2. restore into the throw-away database ---------------------------------------------------
    & (Join-Path $PSScriptRoot "restore-db.ps1") -BackupFile $BackupFile -Database $test -Server $Server
    if ($LASTEXITCODE -ne 0) { throw "restore-db.ps1 failed with exit code $LASTEXITCODE" }
    $restored = $true
    Add-Result "Restore" "PASS" "restored into $test"

    # ---- 3. checks ---------------------------------------------------------------------------------
    try { Invoke-Sql "DBCC CHECKDB ([$test]) WITH NO_INFOMSGS" | Out-Null; Add-Result "DBCC CHECKDB" "PASS" "no errors" }
    catch { Add-Result "DBCC CHECKDB" "FAIL" ($_.Exception.Message -replace '\s+', ' ') }

    $keyTables = @('Branches', 'Permissions', 'Customers', 'Orders', 'OrderItems', 'Products', 'ProductVariants', 'StockLevels', 'Invoices', 'Payments', 'AuditLogs')
    $counts = @()
    $missing = @()
    foreach ($t in $keyTables) {
        $present = Invoke-Sql "SELECT COUNT(*) FROM sys.tables WHERE name = N'$t' AND schema_id = SCHEMA_ID(N'dbo')" $test
        if ($present -ne '1') { $missing += $t; continue }
        $counts += ("{0}={1}" -f $t, (Invoke-Sql "SELECT COUNT_BIG(*) FROM [dbo].[$t]" $test))
    }
    if ($missing.Count -gt 0) { Add-Result "Key tables" "FAIL" ("missing: " + ($missing -join ', ')) }
    else { Add-Result "Key tables" "PASS" ($counts -join '  ') }

    # last migration in the database vs the newest in the code
    $codeLast = $null
    if (Test-Path -LiteralPath $MigrationsPath) {
        $codeLast = Get-ChildItem -LiteralPath $MigrationsPath -Filter "*.Designer.cs" |
            Where-Object { $_.Name -match '^(\d{14}_[A-Za-z0-9_]+)\.Designer\.cs$' } |
            ForEach-Object { $Matches[1] } | Sort-Object | Select-Object -Last 1
    }
    if (-not $codeLast) { Add-Result "Migration history" "FAIL" "could not read the newest migration from $MigrationsPath" }
    else {
        try {
            $dbLast = Invoke-Sql "SELECT TOP 1 MigrationId FROM dbo.__EFMigrationsHistory ORDER BY MigrationId DESC" $test
            if ($dbLast -eq $codeLast) { Add-Result "Migration history" "PASS" "last migration $dbLast equals the code" }
            else { Add-Result "Migration history" "FAIL" "database ends at '$dbLast', the code at '$codeLast' (restore is older than the code: apply migrations before use)" }
        }
        catch { Add-Result "Migration history" "FAIL" ($_.Exception.Message -replace '\s+', ' ') }
    }

    # sample order totals
    if ($missing -contains 'Orders') { Add-Result "Order totals" "FAIL" "Orders table missing" }
    else {
        $sample = [int](Invoke-Sql "SELECT COUNT(*) FROM (SELECT TOP 50 Id FROM dbo.Orders ORDER BY CreatedAt DESC) o" $test)
        if ($sample -eq 0) { Add-Result "Order totals" "SKIP" "no orders in this backup, nothing to check" }
        else {
            $bad = [int](Invoke-Sql ("SELECT COUNT(*) FROM (SELECT TOP 50 * FROM dbo.Orders ORDER BY CreatedAt DESC) o " +
                    "WHERE ABS(o.GrandTotal - (o.Subtotal - o.PromotionDiscount - o.CouponDiscount - o.ManualDiscount + o.DeliveryCharge)) > 0.005") $test)
            if ($bad -eq 0) { Add-Result "Order totals" "PASS" "$sample newest orders: GrandTotal matches the formula" }
            else { Add-Result "Order totals" "FAIL" "$bad of $sample newest orders have a GrandTotal that does not match the formula" }
        }
    }
}
catch {
    Add-Result "Restore test" "FAIL" ($_.Exception.Message -replace '\s+', ' ')
}
finally {
    if ($restored -and -not $KeepDatabase) {
        try {
            Invoke-Sql "ALTER DATABASE [$test] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$test]" | Out-Null
            Write-Host "Dropped throw-away database $test."
        }
        catch { Write-Host "COULD NOT DROP $test : $($_.Exception.Message). Drop it by hand." -ForegroundColor Yellow }
    }
    elseif ($restored) { Write-Host "Kept throw-away database $test (-KeepDatabase). Drop it when done." }
}

Write-Host ""
Write-Host "RESTORE TEST REPORT" -ForegroundColor Cyan
Write-Host ("  Backup : {0}" -f (Split-Path -Leaf $BackupFile))
foreach ($r in $results) {
    $color = switch ($r.Result) { 'PASS' { 'Green' } 'SKIP' { 'Yellow' } default { 'Red' } }
    Write-Host ("  [{0}] {1}: {2}" -f $r.Result, $r.Check, $r.Detail) -ForegroundColor $color
}
$failed = @($results | Where-Object { $_.Result -eq 'FAIL' }).Count
if ($failed -eq 0) { Write-Host "RESULT: PASS" -ForegroundColor Green; exit 0 }
Write-Host "RESULT: FAIL ($failed check(s) failed)" -ForegroundColor Red
exit 1

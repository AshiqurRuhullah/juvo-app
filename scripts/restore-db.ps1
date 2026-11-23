<#
.SYNOPSIS
  Restores a .bak file into a NEW database (no Docker, Windows authentication). Refuses to overwrite by default.

.DESCRIPTION
  1. Checks the file with  RESTORE VERIFYONLY ... WITH CHECKSUM.
  2. Reads the file's logical files with  RESTORE FILELISTONLY  and moves every file into the instance's data and log folders,
     named after the NEW database, so the original database's files are never touched.
  3. Prints exactly what it will do, then restores.

  Without -Database the target is  <original name>_Restored_<yyyyMMddHHmmss>  (always new).
  If the target database already exists the script stops, unless you pass -ReplaceExisting AND type the database name
  (interactively, or with -ConfirmName <name> for automation). Replacing puts the database in SINGLE_USER mode with
  rollback of open connections: stop the application first. This is an operator action; the web API has no restore.

  After restoring a live database, point the application at it (ConnectionStrings:Default) and start it; the application
  will apply any newer migrations in Development, or run  dotnet run --project src/Juvo.Api -- --migrate  in Production.

.PARAMETER BackupFile
  Path of the .bak file (required).

.PARAMETER Database
  Name of the database to create. Default <original>_Restored_<timestamp>.

.PARAMETER Server
  SQL Server instance. Default (localdb)\MSSQLLocalDB.

.PARAMETER ReplaceExisting
  Allow restoring over an existing database of that name (needs the typed confirmation).

.PARAMETER ConfirmName
  The database name typed as confirmation (only needed with -ReplaceExisting; prompts when omitted).

.PARAMETER WhatIfOnly
  Print the plan and the RESTORE statement, change nothing.

.EXAMPLE
  .\scripts\restore-db.ps1 -BackupFile ".\.run\backups\juvo-manual_JuvoDb_20261123T101500Z_ab12cd34.bak"
  .\scripts\restore-db.ps1 -BackupFile $bak -Database JuvoDb_Check
  .\scripts\restore-db.ps1 -BackupFile $bak -Database JuvoDb -ReplaceExisting
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$BackupFile,
    [string]$Database,
    [string]$Server = "(localdb)\MSSQLLocalDB",
    [switch]$ReplaceExisting,
    [string]$ConfirmName,
    [switch]$WhatIfOnly
)

$ErrorActionPreference = "Stop"

if (-not (Get-Command sqlcmd -ErrorAction SilentlyContinue)) {
    throw "sqlcmd was not found on PATH. Install the SQL Server command-line tools (winget install Microsoft.Sqlcmd)."
}
if (-not (Test-Path -LiteralPath $BackupFile -PathType Leaf)) { throw "Backup file not found: $BackupFile" }
$BackupFile = (Resolve-Path -LiteralPath $BackupFile).Path
$sqlFile = $BackupFile.Replace("'", "''")

function Invoke-Sql([string]$Query) {
    $sqlArgs = @('-S', $Server, '-E', '-C', '-b', '-d', 'master', '-h', '-1', '-W', '-s', '|', '-t', 0, '-Q', "SET NOCOUNT ON; $Query")
    $out = & sqlcmd @sqlArgs 2>&1
    if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed: $(($out | Out-String).Trim())" }
    return ($out | Out-String).Trim()
}

# Original database name from the backup header, used for the default new name.
$header = Invoke-Sql "RESTORE HEADERONLY FROM DISK = N'$sqlFile'"
$original = ($header -split "`r?`n" | Select-Object -First 1) -split '\|' | Select-Object -Index 9
if (-not $original -or $original -notmatch '^[A-Za-z_][A-Za-z0-9_]{0,63}$') { $original = "JuvoDb" }

if (-not $Database) { $Database = "${original}_Restored_$([DateTime]::Now.ToString('yyyyMMddHHmmss'))" }
if ($Database -notmatch '^[A-Za-z_][A-Za-z0-9_]{0,63}$') {
    throw "Invalid database name '$Database'. Use letters, digits and underscore only."
}

$exists = (Invoke-Sql "SELECT COUNT(*) FROM sys.databases WHERE name = N'$Database'") -eq '1'
if ($exists -and -not $ReplaceExisting) {
    Write-Host "REFUSED: database '$Database' already exists on $Server. Nothing was changed." -ForegroundColor Red
    Write-Host "Restore into a new name (omit -Database), or pass -ReplaceExisting and type the database name to overwrite it."
    exit 3
}

Write-Host "Step 1/3 Verifying the backup file ..."
try { Invoke-Sql "RESTORE VERIFYONLY FROM DISK = N'$sqlFile' WITH CHECKSUM" | Out-Null; Write-Host "         verified (checksums are good)" -ForegroundColor Green }
catch { Write-Host "REFUSED: the backup file does not verify: $($_.Exception.Message)" -ForegroundColor Red; exit 4 }

Write-Host "Step 2/3 Reading the files inside the backup ..."
$fileList = Invoke-Sql "RESTORE FILELISTONLY FROM DISK = N'$sqlFile'"
$files = @()
foreach ($line in ($fileList -split "`r?`n")) {
    $cols = $line -split '\|'
    if ($cols.Count -ge 3 -and $cols[2] -in @('D', 'L')) { $files += [pscustomobject]@{ Logical = $cols[0]; Physical = $cols[1]; Type = $cols[2] } }
}
if ($files.Count -eq 0) { throw "RESTORE FILELISTONLY returned no data or log files." }

$dataDir = Invoke-Sql "SELECT CAST(COALESCE(SERVERPROPERTY('InstanceDefaultDataPath'), N'') AS nvarchar(260))"
$logDir = Invoke-Sql "SELECT CAST(COALESCE(SERVERPROPERTY('InstanceDefaultLogPath'), N'') AS nvarchar(260))"
if (-not $dataDir) {
    $dataDir = Split-Path -Parent (Invoke-Sql "SELECT TOP 1 physical_name FROM sys.master_files WHERE database_id = 1 AND file_id = 1")
}
if (-not $logDir) { $logDir = $dataDir }

$moves = @()
$n = 0
foreach ($f in $files) {
    $n++
    $ext = if ($f.Type -eq 'L') { '.ldf' } elseif ($n -eq 1) { '.mdf' } else { '.ndf' }
    $dir = if ($f.Type -eq 'L') { $logDir } else { $dataDir }
    $target = Join-Path $dir ("{0}_{1}{2}" -f $Database, $f.Logical, $ext)
    $moves += "MOVE N'$($f.Logical.Replace("'", "''"))' TO N'$($target.Replace("'", "''"))'"
    Write-Host ("         {0}  ->  {1}" -f $f.Logical, $target)
}

$with = ($moves + @("CHECKSUM", "STATS = 25")) -join ", "
if ($exists) { $with += ", REPLACE" }
$restoreSql = "RESTORE DATABASE [$Database] FROM DISK = N'$sqlFile' WITH $with"

Write-Host ""
Write-Host "PLAN" -ForegroundColor Cyan
Write-Host "  Server   : $Server"
Write-Host "  Backup   : $BackupFile  (database '$original' when it was backed up)"
Write-Host "  Target   : $Database  ($(if ($exists) { 'EXISTING database will be OVERWRITTEN' } else { 'new database' }))"
Write-Host "  Statement: $restoreSql"
if ($WhatIfOnly) { Write-Host "-WhatIfOnly given: nothing was changed."; exit 0 }

if ($exists) {
    $typed = if ($ConfirmName) { $ConfirmName } else { Read-Host "This will OVERWRITE '$Database'. Type the database name to confirm" }
    if ($typed -cne $Database) { Write-Host "REFUSED: the confirmation did not match. Nothing was changed." -ForegroundColor Red; exit 5 }
    Write-Host "Step 3/3 Closing open connections and restoring over '$Database' ..."
    Invoke-Sql "ALTER DATABASE [$Database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE" | Out-Null
    try { Invoke-Sql $restoreSql | Out-Null }
    finally { try { Invoke-Sql "ALTER DATABASE [$Database] SET MULTI_USER" | Out-Null } catch { Write-Host "Could not return to MULTI_USER: $($_.Exception.Message)" -ForegroundColor Yellow } }
}
else {
    Write-Host "Step 3/3 Restoring into the new database '$Database' ..."
    Invoke-Sql $restoreSql | Out-Null
}

$state = Invoke-Sql "SELECT state_desc FROM sys.databases WHERE name = N'$Database'"
$tables = Invoke-Sql "SET NOCOUNT ON; SELECT COUNT(*) FROM [$Database].sys.tables"
Write-Host "RESTORED: '$Database' is $state with $tables tables." -ForegroundColor Green
exit 0

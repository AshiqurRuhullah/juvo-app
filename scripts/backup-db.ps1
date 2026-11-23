<#
.SYNOPSIS
  Takes a verified backup of a SQL Server database into a folder (no Docker, Windows authentication).

.DESCRIPTION
  Runs  BACKUP DATABASE ... WITH CHECKSUM, INIT  (adds COMPRESSION when the edition supports it; LocalDB and Express do not)
  and then  RESTORE VERIFYONLY ... WITH CHECKSUM  on the new file. It prints the file, its size and whether it verified,
  and exits with code 0 only when the file was written AND verified.

  The file is written by the SQL Server service, so the folder must be writable by that service account
  (LocalDB: your own Windows user). A backup on the same disk as the database is not disaster recovery: copy it elsewhere.

  File names look like  juvo-manual_<database>_<yyyyMMddTHHmmssZ>_<8 hex>.bak  and never contain personal data. The
  application's own nightly files use the prefix  juvo-backup_  and are the only ones its retention job deletes, so files made
  by this script are never removed automatically.

.PARAMETER Database
  Database to back up (letters, digits, underscore). Default JuvoDb.

.PARAMETER Folder
  Target folder (created if missing). Default: .run\backups under the repository root.

.PARAMETER Server
  SQL Server instance. Default (localdb)\MSSQLLocalDB.

.PARAMETER NoCompression
  Never use COMPRESSION.

.EXAMPLE
  .\scripts\backup-db.ps1
  .\scripts\backup-db.ps1 -Database JuvoDb -Folder "D:\JuvoBackups" -Server ".\SQLEXPRESS"
#>
[CmdletBinding()]
param(
    [string]$Database = "JuvoDb",
    [string]$Folder,
    [string]$Server = "(localdb)\MSSQLLocalDB",
    [switch]$NoCompression
)

$ErrorActionPreference = "Stop"

if ($Database -notmatch '^[A-Za-z_][A-Za-z0-9_]{0,63}$') {
    throw "Invalid database name '$Database'. Use letters, digits and underscore only."
}
if (-not (Get-Command sqlcmd -ErrorAction SilentlyContinue)) {
    throw "sqlcmd was not found on PATH. Install the SQL Server command-line tools (winget install Microsoft.Sqlcmd)."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Folder) { $Folder = Join-Path $repoRoot ".run\backups" }
$Folder = [System.IO.Path]::GetFullPath($Folder)

function Invoke-Sql([string]$Query, [int]$TimeoutSeconds = 0) {
    $sqlArgs = @('-S', $Server, '-E', '-C', '-b', '-d', 'master', '-h', '-1', '-W', '-t', $TimeoutSeconds, '-Q', "SET NOCOUNT ON; $Query")
    $out = & sqlcmd @sqlArgs 2>&1
    if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed: $(($out | Out-String).Trim())" }
    return ($out | Out-String).Trim()
}

$exists = Invoke-Sql "SELECT COUNT(*) FROM sys.databases WHERE name = N'$Database'"
if ($exists -ne '1') { throw "Database '$Database' does not exist on $Server." }

New-Item -ItemType Directory -Force -Path $Folder | Out-Null

$stamp = [DateTime]::UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'")
$suffix = [Guid]::NewGuid().ToString("N").Substring(0, 8)
$file = Join-Path $Folder "juvo-manual_${Database}_${stamp}_${suffix}.bak"
$sqlPath = $file.Replace("'", "''")

$edition = [int](Invoke-Sql "SELECT CAST(SERVERPROPERTY('EngineEdition') AS int)")
$compress = (-not $NoCompression) -and ($edition -ne 4)   # 4 = Express (also LocalDB): cannot create compressed backups
$with = if ($compress) { "CHECKSUM, COMPRESSION, INIT" } else { "CHECKSUM, INIT" }

Write-Host "Server    : $Server"
Write-Host "Database  : $Database"
Write-Host "File      : $file"
Write-Host "Options   : $with$(if (-not $compress) { '  (compression not used: ' + $(if ($NoCompression) { 'switched off' } else { 'Express/LocalDB edition' }) + ')' })"

try {
    Invoke-Sql "BACKUP DATABASE [$Database] TO DISK = N'$sqlPath' WITH $with" | Out-Null
}
catch {
    Write-Host "BACKUP FAILED: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

if (-not (Test-Path -LiteralPath $file)) {
    Write-Host "BACKUP reported success but the file is not visible to this account (SQL Server may be on another machine). Not verified here." -ForegroundColor Yellow
}
else {
    Write-Host ("Size      : {0:N0} bytes" -f (Get-Item -LiteralPath $file).Length)
}

try {
    Invoke-Sql "RESTORE VERIFYONLY FROM DISK = N'$sqlPath' WITH CHECKSUM" | Out-Null
    Write-Host "Verified  : yes (RESTORE VERIFYONLY ... WITH CHECKSUM passed)" -ForegroundColor Green
}
catch {
    Write-Host "Verified  : NO - $($_.Exception.Message)" -ForegroundColor Red
    exit 2
}

Write-Host "Done. Copy this file off this machine too: a backup on the same disk does not survive a disk failure."
exit 0

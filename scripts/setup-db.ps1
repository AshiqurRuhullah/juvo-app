<#
.SYNOPSIS
  Creates the Juvo database on a local Microsoft SQL Server (no Docker).

.DESCRIPTION
  Finds a reachable local SQL Server using Windows authentication, in this order:
    1. the server passed with -Server
    2. localhost
    3. .\SQLEXPRESS
    4. (localdb)\MSSQLLocalDB
  Then creates the database (default JuvoDb, Unicode collation) if it does not exist and prints
  the connection string to store in user-secrets (never commit it).
  Safe to run many times: it never drops or alters an existing database.

.EXAMPLE
  .\scripts\setup-db.ps1
  .\scripts\setup-db.ps1 -Server ".\SQLEXPRESS" -Database JuvoDb
  .\scripts\setup-db.ps1 -Database JuvoDb_Test     # separate database for integration tests
#>
[CmdletBinding()]
param(
    [string]$Server,
    [string]$Database = "JuvoDb"
)

$ErrorActionPreference = "Stop"

if ($Database -notmatch '^[A-Za-z_][A-Za-z0-9_]{0,63}$') {
    throw "Invalid database name '$Database'. Use letters, digits and underscore only."
}

$sqlcmd = Get-Command sqlcmd -ErrorAction SilentlyContinue
if (-not $sqlcmd) {
    Write-Host ""
    Write-Host "sqlcmd was not found on PATH." -ForegroundColor Red
    Write-Host "Install the SQL Server command-line tools, or install SQL Server Developer/Express:"
    Write-Host "  winget install Microsoft.SQLServer.2022.Express   (or Developer edition)"
    Write-Host "  winget install Microsoft.Sqlcmd"
    Write-Host "Then open a new terminal and run this script again."
    exit 1
}

$candidates = @()
if ($Server) { $candidates += $Server }
$candidates += @("localhost", ".\SQLEXPRESS", "(localdb)\MSSQLLocalDB")

$found = $null
foreach ($candidate in $candidates) {
    Write-Host "Trying $candidate ..." -NoNewline
    # An unreachable instance writes to stderr; Windows PowerShell 5.1 would turn that into a terminating error.
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    & sqlcmd -S $candidate -E -C -l 10 -b -Q "SET NOCOUNT ON; SELECT 1" 2>$null | Out-Null
    $probeExit = $LASTEXITCODE
    $ErrorActionPreference = $previousPreference
    if ($probeExit -eq 0) {
        Write-Host " reachable" -ForegroundColor Green
        $found = $candidate
        break
    }
    Write-Host " not reachable" -ForegroundColor DarkYellow
}

if (-not $found) {
    Write-Host ""
    Write-Host "No local SQL Server was reachable." -ForegroundColor Red
    Write-Host "Make sure SQL Server (Developer/Express) or LocalDB is installed and its service is running:"
    Write-Host "  Get-Service MSSQL*            # service names and state"
    Write-Host "  SqlLocalDB.exe info           # LocalDB instances"
    Write-Host "  winget install Microsoft.SQLServer.2022.Express"
    Write-Host "Or pass the server explicitly: .\scripts\setup-db.ps1 -Server ""MYPC\INSTANCE"""
    exit 1
}

$sql = @"
SET NOCOUNT ON;
IF DB_ID(N'$Database') IS NULL
BEGIN
    CREATE DATABASE [$Database] COLLATE Latin1_General_100_CI_AS_SC_UTF8;
    SELECT 'created' AS result;
END
ELSE
    SELECT 'exists' AS result;
"@

# UTF8 collation needs SQL Server 2019+. Fall back to the server default if not supported.
$result = & sqlcmd -S $found -E -C -b -h -1 -W -Q $sql 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "UTF-8 collation not accepted ($result). Retrying with the server default collation (nvarchar still stores Bengali correctly)." -ForegroundColor DarkYellow
    $fallback = "SET NOCOUNT ON; IF DB_ID(N'$Database') IS NULL BEGIN CREATE DATABASE [$Database]; SELECT 'created'; END ELSE SELECT 'exists';"
    $result = & sqlcmd -S $found -E -C -b -h -1 -W -Q $fallback
    if ($LASTEXITCODE -ne 0) { throw "Could not create database: $result" }
}

Write-Host ""
Write-Host "Database [$Database] on $found : $($result | Select-Object -First 1)" -ForegroundColor Green

$version = & sqlcmd -S $found -E -C -h -1 -W -Q "SET NOCOUNT ON; SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(50)) + ' ' + CAST(SERVERPROPERTY('Edition') AS nvarchar(100))"
Write-Host "Server version: $($version | Select-Object -First 1)"

$conn = "Server=$found;Database=$Database;Trusted_Connection=True;TrustServerCertificate=True"
Write-Host ""
Write-Host "Connection string (Windows authentication, local development only):"
Write-Host "  $conn"
Write-Host ""
Write-Host "Store it with user-secrets once the API project exists (Step 2):"
Write-Host "  dotnet user-secrets set ""ConnectionStrings:Default"" ""$conn"" --project juvo-be/src/Juvo.Api"

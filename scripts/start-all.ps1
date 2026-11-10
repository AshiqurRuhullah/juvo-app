<#
.SYNOPSIS
  One command to run the whole Juvo BD system on this machine (no Docker): database, API and web app.

.DESCRIPTION
  1. Checks the tools (.NET 10 SDK, Node.js, sqlcmd) and says exactly what to install when one is missing.
  2. Creates the database JuvoDb if needed (scripts\setup-db.ps1; safe to run again, never drops anything).
  3. Creates juvo-front\.env.local from .env.example when it is missing and runs `npm install` when packages are missing.
  4. Builds and starts the API (Development: applies migrations, seeds roles, branches, the admin and the sample catalog).
  5. Starts the web app (development server) and waits until both answer.
  Output of each part goes to .run\api.log and .run\web.log. Stop everything with:  .\scripts\start-all.ps1 -Stop

.EXAMPLE
  .\scripts\start-all.ps1                 # first run and every later run
  .\scripts\start-all.ps1 -Open           # also opens the login page in the browser
  .\scripts\start-all.ps1 -Stop           # stops the API and the web app started by this script
  .\scripts\start-all.ps1 -Status         # shows what is running
  .\scripts\start-all.ps1 -SqlServer ".\SQLEXPRESS"   # use another SQL Server instance
#>
[CmdletBinding()]
param(
    [switch]$Stop,
    [switch]$Status,
    [switch]$Open,
    [switch]$SkipInstall,
    [string]$SqlServer
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$runDir = Join-Path $root ".run"
$pidFile = Join-Path $runDir "pids.json"
$apiUrl = "http://localhost:5080"
$webUrl = "http://localhost:3000"

function Say($text, $color = "Gray") { Write-Host $text -ForegroundColor $color }
function Step($text) { Write-Host ""; Write-Host "==> $text" -ForegroundColor Cyan }
function Fail($text) { Write-Host ""; Write-Host $text -ForegroundColor Red; exit 1 }

function Test-Url($url) {
    try { return (Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 3).StatusCode -lt 500 } catch { return $false }
}

function Wait-Url($url, $name, $seconds, $logFile) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Url $url) { return }
        Start-Sleep -Seconds 2
    }
    Say "Last lines of ${logFile}:" "Yellow"
    if (Test-Path $logFile) { Get-Content $logFile -Tail 15 | ForEach-Object { Say "  $_" "Yellow" } }
    Fail "$name did not answer at $url within $seconds seconds."
}

function Read-Pids {
    if (Test-Path $pidFile) { return Get-Content $pidFile -Raw | ConvertFrom-Json }
    return $null
}

function Stop-Tree($processId) {
    if ($processId -and (Get-Process -Id $processId -ErrorAction SilentlyContinue)) {
        & taskkill /PID $processId /T /F | Out-Null
    }
}

# ---- Stop / Status ---------------------------------------------------------------------------------------------------
if ($Stop) {
    $pids = Read-Pids
    if (-not $pids) { Say "Nothing was started by this script."; exit 0 }
    Stop-Tree $pids.web
    Stop-Tree $pids.api
    Remove-Item $pidFile -ErrorAction SilentlyContinue
    Say "Stopped the web app and the API." "Green"
    exit 0
}

if ($Status) {
    Say ("API  {0,-28} {1}" -f $apiUrl, $(if (Test-Url "$apiUrl/health/live") { "running" } else { "not running" }))
    Say ("Web  {0,-28} {1}" -f $webUrl, $(if (Test-Url "$webUrl/login") { "running" } else { "not running" }))
    exit 0
}

# ---- 1. Tools --------------------------------------------------------------------------------------------------------
Step "Checking the tools"
$nodeDir = Join-Path $env:ProgramFiles "nodejs"
if (-not (Get-Command node -ErrorAction SilentlyContinue) -and (Test-Path (Join-Path $nodeDir "node.exe"))) {
    $env:Path = "$nodeDir;$env:Path" # Node is installed but this window started before the installer changed the PATH
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Fail "The .NET SDK was not found. Install it with:  winget install Microsoft.DotNet.SDK.10   then open a new terminal."
}
$dotnetVersion = (& dotnet --version).Trim()
if ([int]($dotnetVersion.Split(".")[0]) -lt 10) { Fail ".NET SDK $dotnetVersion found, but version 10 or newer is needed:  winget install Microsoft.DotNet.SDK.10" }
Say "  .NET SDK $dotnetVersion"

if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
    Fail "Node.js was not found. Install it with:  winget install OpenJS.NodeJS.LTS   then open a new terminal."
}
Say "  Node.js $((& node --version).Trim()), npm $((& npm --version).Trim())"

if (-not (Get-Command sqlcmd -ErrorAction SilentlyContinue)) {
    Fail "sqlcmd was not found. Install SQL Server Express/LocalDB and the tools:  winget install Microsoft.SQLServer.2022.Express ; winget install Microsoft.Sqlcmd   then open a new terminal."
}

if ((Test-Url "$apiUrl/health/live") -or (Test-Url "$webUrl/login")) {
    Say "  Something already answers on port 5080 or 3000. Run  .\scripts\start-all.ps1 -Status  to see it, or  -Stop  to stop what this script started." "Yellow"
    if (-not (Read-Pids)) { Fail "Free ports 5080 and 3000 (close the old API or web app) and run this script again." }
    Say "  Restarting what this script started earlier." "Yellow"
    & $PSCommandPath -Stop | Out-Null
    Start-Sleep -Seconds 2
}

# ---- 2. Database -----------------------------------------------------------------------------------------------------
Step "Database"
if ($SqlServer) { & (Join-Path $PSScriptRoot "setup-db.ps1") -Server $SqlServer } else { & (Join-Path $PSScriptRoot "setup-db.ps1") }
if ($LASTEXITCODE -ne 0) { Fail "The database could not be prepared (see the message above)." }

# ---- 3. Settings and packages ----------------------------------------------------------------------------------------
Step "Settings and packages"
$front = Join-Path $root "juvo-front"
$envLocal = Join-Path $front ".env.local"
if (-not (Test-Path $envLocal)) {
    Copy-Item (Join-Path $front ".env.example") $envLocal
    Say "  Created juvo-front\.env.local from .env.example (no secrets in it)."
} else {
    Say "  juvo-front\.env.local already exists."
}
if (-not $SkipInstall -and -not (Test-Path (Join-Path $front "node_modules"))) {
    Say "  Installing front-end packages (first run only, a few minutes)..."
    Push-Location $front
    try { & npm install --no-audit --no-fund; if ($LASTEXITCODE -ne 0) { Fail "npm install failed." } } finally { Pop-Location }
} else {
    Say "  Front-end packages are in place."
}

# ---- 4. API ----------------------------------------------------------------------------------------------------------
Step "Starting the API"
New-Item -ItemType Directory -Force -Path $runDir | Out-Null
$apiLog = Join-Path $runDir "api.log"
$webLog = Join-Path $runDir "web.log"
$be = Join-Path $root "juvo-be"
Say "  Building (first build takes a minute)..."
& dotnet build (Join-Path $be "Juvo.sln") --nologo -v quiet
if ($LASTEXITCODE -ne 0) { Fail "The API did not build (see the messages above)." }

$env:ASPNETCORE_ENVIRONMENT = "Development"
$api = Start-Process -FilePath "dotnet" `
    -ArgumentList @("run", "--no-build", "--no-launch-profile", "--project", ('"' + (Join-Path $be "src\Juvo.Api") + '"'), "--urls", $apiUrl) `
    -WorkingDirectory $be -WindowStyle Hidden -RedirectStandardOutput $apiLog -RedirectStandardError "$apiLog.err" -PassThru
Wait-Url "$apiUrl/health/ready" "The API" 180 $apiLog
Say "  API ready at $apiUrl (interactive docs: $apiUrl/scalar/v1)" "Green"

# ---- 5. Web app ------------------------------------------------------------------------------------------------------
Step "Starting the web app"
$env:API_BASE_URL = $apiUrl
$web = Start-Process -FilePath "cmd.exe" -ArgumentList @("/c", "npm run dev") `
    -WorkingDirectory $front -WindowStyle Hidden -RedirectStandardOutput $webLog -RedirectStandardError "$webLog.err" -PassThru
@{ api = $api.Id; web = $web.Id; started = (Get-Date).ToString("s") } | ConvertTo-Json | Set-Content $pidFile
Wait-Url "$webUrl/login" "The web app" 180 $webLog
Say "  Web app ready at $webUrl" "Green"

Write-Host ""
Say "Everything is running." "Green"
Say "  Shop and admin   $webUrl            (admin: $webUrl/login)"
Say "  API docs         $apiUrl/scalar/v1"
Say "  Admin sign-in    admin@juvobd.local  /  Juvo@Dev#2026   (development only; you must change the password at first sign-in)"
Say "  Sample catalog   16 demo products are loaded (tag 'sample')"
Say "  Logs             .run\api.log and .run\web.log"
Say "  Stop             .\scripts\start-all.ps1 -Stop"
if ($Open) { Start-Process "$webUrl/login" }

<#
.SYNOPSIS
  Zips the uploaded files folder (product pictures, return photos, ticket attachments) with a time stamp.

.DESCRIPTION
  A database backup does not contain the files under /files (the Storage:RootPath folder). This script copies that folder into
  juvo-files-<yyyyMMddTHHmmssZ>.zip in the target folder, using the built-in Compress-Archive, then opens the zip to check it
  can be read and that the number of entries equals the number of source files. It exits 0 only when that check passes.
  Files are read as they are: take the zip when few uploads are happening (for example at night, after the nightly database backup).

  Like database backups, a zip on the same disk is not disaster recovery: copy it elsewhere.

.PARAMETER Source
  The uploads folder. Default: juvo-be\src\Juvo.Api\App_Data\uploads (the Storage:RootPath default). Use the real path in production.

.PARAMETER Folder
  Where to write the zip (created if missing). Default: .run\backups under the repository root.

.PARAMETER KeepDays
  Delete juvo-files-*.zip files older than this many days in the target folder (0 = keep everything, the default). Only files with that exact name pattern are touched.

.EXAMPLE
  .\scripts\backup-files.ps1
  .\scripts\backup-files.ps1 -Source "D:\Juvo\uploads" -Folder "D:\JuvoBackups" -KeepDays 30
#>
[CmdletBinding()]
param(
    [string]$Source,
    [string]$Folder,
    [int]$KeepDays = 0
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Source) { $Source = Join-Path $repoRoot "juvo-be\src\Juvo.Api\App_Data\uploads" }
if (-not $Folder) { $Folder = Join-Path $repoRoot ".run\backups" }
$Source = [System.IO.Path]::GetFullPath($Source)
$Folder = [System.IO.Path]::GetFullPath($Folder)

if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
    Write-Host "Nothing to back up: the uploads folder does not exist: $Source" -ForegroundColor Yellow
    exit 0
}

$files = @(Get-ChildItem -LiteralPath $Source -Recurse -File)
if ($files.Count -eq 0) {
    Write-Host "Nothing to back up: the uploads folder is empty: $Source" -ForegroundColor Yellow
    exit 0
}

New-Item -ItemType Directory -Force -Path $Folder | Out-Null
$stamp = [DateTime]::UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'")
$zip = Join-Path $Folder "juvo-files-$stamp.zip"
if ($Folder.StartsWith($Source, [StringComparison]::OrdinalIgnoreCase)) { throw "The target folder must not be inside the uploads folder." }

Write-Host "Source : $Source ($($files.Count) files, $('{0:N0}' -f ($files | Measure-Object Length -Sum).Sum) bytes)"
Write-Host "Zip    : $zip"

Compress-Archive -Path (Join-Path $Source '*') -DestinationPath $zip -CompressionLevel Optimal

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
try { $entries = @($archive.Entries | Where-Object { $_.Name -ne '' }).Count } finally { $archive.Dispose() }

Write-Host ("Size   : {0:N0} bytes, {1} entries" -f (Get-Item -LiteralPath $zip).Length, $entries)
if ($entries -ne $files.Count) {
    Write-Host "CHECK FAILED: the zip has $entries entries but the folder had $($files.Count) files (files changed while zipping?). Run it again." -ForegroundColor Red
    exit 1
}
Write-Host "Verified: the zip opens and holds every file." -ForegroundColor Green

if ($KeepDays -gt 0) {
    $cutoff = (Get-Date).AddDays(-$KeepDays)
    Get-ChildItem -LiteralPath $Folder -File | Where-Object { $_.Name -match '^juvo-files-\d{8}T\d{6}Z\.zip$' -and $_.LastWriteTime -lt $cutoff } |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force; Write-Host "Removed old archive $($_.Name)" }
}

Write-Host "Done. Copy this zip off this machine too."
exit 0

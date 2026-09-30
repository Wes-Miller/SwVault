<#
.SYNOPSIS
  Nightly backup of a Windows-service Gitea (shop PC option). Schedule it with Task Scheduler,
  e.g. daily at 02:15, running as an administrator account:
    powershell -NoProfile -ExecutionPolicy Bypass -File C:\SwVault\server\backup\backup-gitea.ps1 -Root D:\SwVaultServer -Destination E:\SwVaultBackup

.DESCRIPTION
  - gitea dump (database, config, repositories; small) into Destination\daily, keeping 14.
  - Robocopy mirror of the LFS content store (the large, append-only part) into Destination\lfs.
  Copy Destination to a second location (another drive, CU storage) regularly, and practice a
  restore (server\README.md) once a semester.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Root,
    [Parameter(Mandatory)] [string]$Destination
)

$ErrorActionPreference = 'Stop'
$exe = Join-Path $Root 'gitea.exe'
$ini = Join-Path $Root 'app.ini'
$data = Join-Path $Root 'data'
$daily = Join-Path $Destination 'daily'
$lfs = Join-Path $Destination 'lfs'
New-Item -ItemType Directory -Force -Path $daily, $lfs | Out-Null

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$dump = Join-Path $daily "gitea-$stamp.zip"
& $exe --config $ini --work-path $data dump --skip-lfs-data --type zip --file $dump
if ($LASTEXITCODE -ne 0) { throw 'gitea dump failed.' }

# /E copies new objects only; LFS objects are content-addressed and never modified.
robocopy (Join-Path $data 'lfs') $lfs /E /R:2 /W:5 /NP /NFL /NDL | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE." }

Get-ChildItem $daily -Filter 'gitea-*.zip' | Sort-Object LastWriteTime -Descending | Select-Object -Skip 14 | Remove-Item
Write-Host "Backup $stamp complete."

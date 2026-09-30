<#
.SYNOPSIS
  Removes SwVault from this PC. Your vault folder (e.g. C:\SWVault\...) and anything checked in
  on the server are not touched. Check in or undo your check-outs first.
#>
[CmdletBinding()]
param([string]$Target = (Join-Path $env:ProgramFiles 'SwVault'))

$ErrorActionPreference = 'Stop'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Start-Process powershell -Verb RunAs -Wait -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Target `"$Target`""
    return
}
if (Get-Process SLDWORKS -ErrorAction SilentlyContinue) { throw 'Close SOLIDWORKS first.' }

Get-Process SwVault.Agent -ErrorAction SilentlyContinue | Stop-Process -Force
$dll = Join-Path $Target 'addin\SwVault.AddIn.dll'
if (Test-Path $dll) {
    & (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe') /u /nologo $dll
}
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'SwVaultAgent' -ErrorAction SilentlyContinue
Remove-ItemProperty -Path 'HKCU:\Software\SwVault' -Name 'AgentPath' -ErrorAction SilentlyContinue
$binDir = Join-Path $Target 'bin'
$machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
[Environment]::SetEnvironmentVariable('Path', (($machinePath -split ';') | Where-Object { $_ -and $_ -ne $binDir }) -join ';', 'Machine')
Remove-Item $Target -Recurse -Force -ErrorAction SilentlyContinue
Write-Host 'SwVault removed. Your local vault folder and settings in %LOCALAPPDATA%\SwVault were kept.'

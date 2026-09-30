<#
.SYNOPSIS
  Installs SwVault on this PC (run from the unzipped package; asks for administrator rights).

.DESCRIPTION
  - Copies SwVault to C:\Program Files\SwVault (bin\ = agent + CLI, addin\ = SOLIDWORKS add-in).
  - Registers the SOLIDWORKS add-in (RegAsm /codebase).
  - Starts the SwVault agent at sign-in for the installing user and adds swvault to PATH.
  Requires Git for Windows 2.31+ (https://git-scm.com/download/win, or: winget install Git.Git).
#>
[CmdletBinding()]
param([string]$Target = (Join-Path $env:ProgramFiles 'SwVault'))

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Start-Process powershell -Verb RunAs -Wait -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Target `"$Target`""
    return
}

$git = Get-Command git -ErrorAction SilentlyContinue
if (-not $git) {
    Write-Warning 'Git for Windows is not installed. Install it first: winget install Git.Git   (then run this installer again)'
    Read-Host 'Press Enter to exit'
    exit 1
}
if (Get-Process SLDWORKS -ErrorAction SilentlyContinue) {
    Write-Warning 'Close SOLIDWORKS before installing.'
    Read-Host 'Press Enter to exit'
    exit 1
}

Get-Process SwVault.Agent -ErrorAction SilentlyContinue | Stop-Process -Force
New-Item -ItemType Directory -Force -Path $Target | Out-Null
Copy-Item (Join-Path $here 'bin') $Target -Recurse -Force
Copy-Item (Join-Path $here 'addin') $Target -Recurse -Force

$regasm = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe'
& $regasm /codebase /nologo (Join-Path $Target 'addin\SwVault.AddIn.dll')
if ($LASTEXITCODE -ne 0) { throw 'Registering the SOLIDWORKS add-in failed.' }

$agent = Join-Path $Target 'bin\SwVault.Agent.exe'
New-Item -Path 'HKCU:\Software\SwVault' -Force | Out-Null
Set-ItemProperty -Path 'HKCU:\Software\SwVault' -Name 'AgentPath' -Value $agent
Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'SwVaultAgent' -Value "`"$agent`""

$machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
$binDir = Join-Path $Target 'bin'
if ($machinePath -notlike "*$binDir*") { [Environment]::SetEnvironmentVariable('Path', "$machinePath;$binDir", 'Machine') }

Start-Process $agent
Write-Host ''
Write-Host 'SwVault installed.'
Write-Host 'Next: click the SwVault tray icon > Vaults... and connect with the URL, user name and token from your admin.'
Write-Host 'Then open SOLIDWORKS: the SwVault tab and task pane are ready.'
Read-Host 'Press Enter to close'

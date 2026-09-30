<#
.SYNOPSIS
  Installs SwVault on this PC. Run from the unzipped package (double-click "Install SwVault.cmd");
  asks for administrator rights once.

.DESCRIPTION
  Machine part (elevated):
  - Copies SwVault to C:\Program Files\SwVault (bin\ = agent, CLI and bundled Git; addin\ = SOLIDWORKS add-in).
  - Registers the SOLIDWORKS add-in (RegAsm /codebase) and adds swvault to PATH.
  User part (not elevated, so it lands in your own account even if an admin approved the prompt):
  - Starts the SwVault agent at sign-in, and starts it now.
  - Team packages (bin\team.json): the agent immediately asks for your user name and password
    and then downloads the vault. Nothing else to set up.
#>
[CmdletBinding()]
param(
    [string]$Target = (Join-Path $env:ProgramFiles 'SwVault'),
    [switch]$MachineOnly
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

function Pause-Exit([int]$Code) {
    Read-Host 'Press Enter to close'
    exit $Code
}

if (-not $MachineOnly) {
    if (Get-Process SLDWORKS -ErrorAction SilentlyContinue) {
        Write-Warning 'Close SOLIDWORKS before installing, then run the installer again.'
        Pause-Exit 1
    }
    $bundledGit = Test-Path (Join-Path $here 'bin\git\cmd\git.exe')
    if (-not $bundledGit -and -not (Get-Command git -ErrorAction SilentlyContinue)) {
        Write-Warning 'This package has no bundled Git and Git for Windows is not installed. Install it first (winget install Git.Git), then run this installer again.'
        Pause-Exit 1
    }
    Get-Process SwVault.Agent -ErrorAction SilentlyContinue | Stop-Process -Force
}

# ---------------------------------------------------------------- machine part (elevated)
if ($isAdmin) {
    New-Item -ItemType Directory -Force -Path $Target | Out-Null
    # Replace bin\ entirely so an old bundled Git or team.json doesn't linger.
    $binTarget = Join-Path $Target 'bin'
    if (Test-Path $binTarget) { Remove-Item $binTarget -Recurse -Force }
    Copy-Item (Join-Path $here 'bin') $Target -Recurse -Force
    Copy-Item (Join-Path $here 'addin') $Target -Recurse -Force
    Copy-Item (Join-Path $here 'uninstall.ps1') $Target -Force

    $regasm = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe'
    & $regasm /codebase /nologo (Join-Path $Target 'addin\SwVault.AddIn.dll')
    if ($LASTEXITCODE -ne 0) { throw 'Registering the SOLIDWORKS add-in failed.' }

    $machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $binDir = Join-Path $Target 'bin'
    if ($machinePath -notlike "*$binDir*") { [Environment]::SetEnvironmentVariable('Path', "$machinePath;$binDir", 'Machine') }
} else {
    $p = Start-Process powershell -Verb RunAs -Wait -PassThru -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Target `"$Target`" -MachineOnly"
    if ($p.ExitCode -ne 0) {
        Write-Warning 'The administrator part of the install did not finish.'
        Pause-Exit 1
    }
}
if ($MachineOnly) { exit 0 }

# ---------------------------------------------------------------- user part
$agent = Join-Path $Target 'bin\SwVault.Agent.exe'
New-Item -Path 'HKCU:\Software\SwVault' -Force | Out-Null
Set-ItemProperty -Path 'HKCU:\Software\SwVault' -Name 'AgentPath' -Value $agent
Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'SwVaultAgent' -Value "`"$agent`""

# Start the agent without elevation (explorer launches it as the signed-in user).
if ($isAdmin) { Start-Process explorer.exe -ArgumentList "`"$agent`"" } else { Start-Process $agent }

$team = Join-Path $Target 'bin\team.json'
Write-Host ''
Write-Host 'SwVault installed.'
if (Test-Path $team) {
    $name = (Get-Content $team -Raw | ConvertFrom-Json).name
    Write-Host "A SwVault window is asking for your $name user name and password. Sign in, and your files start downloading."
    Write-Host 'Then open SOLIDWORKS: the SwVault tab and task pane are ready.'
} else {
    Write-Host 'Next: click the SwVault tray icon > Vaults... and connect with the URL, user name and token from your admin.'
    Write-Host 'Then open SOLIDWORKS: the SwVault tab and task pane are ready.'
}
Read-Host 'Press Enter to close'

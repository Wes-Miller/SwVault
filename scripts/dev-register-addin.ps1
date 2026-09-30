<#
.SYNOPSIS
  Builds SwVault and installs the add-in + agent for development on this PC.

.DESCRIPTION
  1. Builds the add-in (net48) and the agent (net10) in Debug.
  2. Copies them to C:\SwVaultDev\AddIn and C:\SwVaultDev\Agent (outside the build folder, so
     rebuilding never fights a DLL that SOLIDWORKS or the agent has loaded).
  3. Records the agent location in HKCU\Software\SwVault\AgentPath (the add-in starts it from there).
  4. Registers the add-in with SOLIDWORKS (RegAsm /codebase). This one step needs administrator
     rights; a UAC prompt appears. Later runs skip it unless -Register is given.

  Close SOLIDWORKS before running. Use -Unregister to remove the add-in.

.EXAMPLE
  .\scripts\dev-register-addin.ps1            # build, copy, register on first run
  .\scripts\dev-register-addin.ps1 -Register  # force re-registration
  .\scripts\dev-register-addin.ps1 -Unregister
#>
[CmdletBinding()]
param(
    [switch]$Register,
    [switch]$Unregister,
    [switch]$RegisterOnly,
    [string]$Configuration = 'Debug',
    [string]$Root = 'C:\SwVaultDev'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$addInDir = Join-Path $Root 'AddIn'
$agentDir = Join-Path $Root 'Agent'
$addInDll = Join-Path $addInDir 'SwVault.AddIn.dll'
$regasm = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe'

function Test-Admin {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Invoke-Elevated([string]$Arguments) {
    $p = Start-Process powershell -Verb RunAs -Wait -PassThru -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" $Arguments -Root `"$Root`""
    if ($p.ExitCode -ne 0) { throw "The elevated step failed (exit code $($p.ExitCode))." }
}

# --- elevated-only steps -------------------------------------------------------------------
if ($RegisterOnly) {
    if (-not (Test-Admin)) { throw 'Run as administrator.' }
    & $regasm /codebase /nologo $addInDll
    if ($LASTEXITCODE -ne 0) { exit 1 }
    exit 0
}

if ($Unregister) {
    if (Get-Process SLDWORKS -ErrorAction SilentlyContinue) { throw 'Close SOLIDWORKS first.' }
    if (Test-Admin) {
        & $regasm /u /nologo $addInDll
    } else {
        Invoke-Elevated '-Unregister'
    }
    Write-Host 'Add-in unregistered.'
    return
}

# --- build and copy (no admin needed) -------------------------------------------------------
if (Get-Process SLDWORKS -ErrorAction SilentlyContinue) { throw 'Close SOLIDWORKS first (it keeps the add-in DLL loaded).' }

function Get-OutputDir([string]$Project) {
    $dir = & dotnet msbuild $Project -getProperty:TargetDir -p:Configuration=$Configuration
    if ($LASTEXITCODE -ne 0) { throw "Could not read the output folder of $Project" }
    return $dir.Trim()
}

$addInProject = Join-Path $repo 'src\SwVault.AddIn\SwVault.AddIn.csproj'
$agentProject = Join-Path $repo 'src\SwVault.Agent\SwVault.Agent.csproj'
foreach ($project in $addInProject, $agentProject) {
    & dotnet build $project -c $Configuration -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
}

# Stop a running dev agent so its files can be replaced.
Get-Process SwVault.Agent -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$agentDir*" } | Stop-Process -Force

New-Item -ItemType Directory -Force -Path $addInDir, $agentDir | Out-Null
Copy-Item -Path (Join-Path (Get-OutputDir $addInProject) '*') -Destination $addInDir -Recurse -Force
Copy-Item -Path (Join-Path (Get-OutputDir $agentProject) '*') -Destination $agentDir -Recurse -Force

$swKey = New-Item -Path 'HKCU:\Software\SwVault' -Force
Set-ItemProperty -Path 'HKCU:\Software\SwVault' -Name 'AgentPath' -Value (Join-Path $agentDir 'SwVault.Agent.exe')
Write-Host "Add-in copied to $addInDir; agent copied to $agentDir"

$registered = Test-Path "Registry::HKEY_LOCAL_MACHINE\SOFTWARE\SolidWorks\Addins\{7F3C2A51-9B4E-4C2D-A6E1-5D8B0F2C9E47}"
if ($Register -or -not $registered) {
    Write-Host 'Registering the add-in with SOLIDWORKS (administrator approval needed)...'
    if (Test-Admin) {
        & $regasm /codebase /nologo $addInDll
    } else {
        Invoke-Elevated '-RegisterOnly'
    }
    Write-Host 'Registered. Start SOLIDWORKS; SwVault appears as a tab and in Tools > Add-Ins.'
} else {
    Write-Host 'Already registered (use -Register to force). Start SOLIDWORKS to load the new build.'
}

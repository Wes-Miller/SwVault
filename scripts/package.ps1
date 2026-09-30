<#
.SYNOPSIS
  Builds a SwVault install package: dist\SwVault-<version>.zip

.DESCRIPTION
  Layout inside the zip:
    bin\       SwVault.Agent.exe and swvault.exe (self-contained .NET 10, no runtime to install)
    bin\git\   MinGit (portable Git for Windows), so members don't have to install Git
    bin\team.json   (with -TeamConfig) which vault to join; members then only sign in
    addin\     SwVault.AddIn.dll (+ SOLIDWORKS interop and protocol DLLs)
    Install SwVault.cmd / install.ps1 / uninstall.ps1 / INSTALL.txt
  Teammates unzip it and double-click "Install SwVault.cmd" (one UAC prompt).

.PARAMETER TeamConfig
  team.json written by server/linux/setup.sh (or swvault-admin.sh client-config).

.PARAMETER MinGitUrl
  MinGit zip to bundle. Default: the newest 64-bit MinGit release of Git for Windows.
  Pass -MinGitUrl none to leave Git out (members then need Git for Windows installed).

.EXAMPLE
  .\scripts\package.ps1 -Version 0.2.0 -TeamConfig .\team.json
#>
[CmdletBinding()]
param(
    [string]$Version = '0.1.0',
    [string]$TeamConfig,
    [string]$MinGitUrl
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$stage = Join-Path $repo "dist\SwVault-$Version"
$zip = Join-Path $repo "dist\SwVault-$Version.zip"
if ($TeamConfig) {
    $teamName = ((Get-Content $TeamConfig -Raw | ConvertFrom-Json).name -replace '[^A-Za-z0-9_-]', '')
    if ($teamName) {
        $stage = Join-Path $repo "dist\SwVault-$teamName-$Version"
        $zip = Join-Path $repo "dist\SwVault-$teamName-$Version.zip"
    }
}
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $stage 'bin'), (Join-Path $stage 'addin') | Out-Null

$common = @('-c', 'Release', "-p:Version=$Version", '-nologo', '-v', 'q')
& dotnet publish (Join-Path $repo 'src\SwVault.Agent\SwVault.Agent.csproj') -r win-x64 --self-contained true -o (Join-Path $stage 'bin') @common
if ($LASTEXITCODE -ne 0) { throw 'Publishing the agent failed.' }
& dotnet publish (Join-Path $repo 'src\SwVault.Cli\SwVault.Cli.csproj') -r win-x64 --self-contained true -o (Join-Path $stage 'bin') @common
if ($LASTEXITCODE -ne 0) { throw 'Publishing the CLI failed.' }
& dotnet build (Join-Path $repo 'src\SwVault.AddIn\SwVault.AddIn.csproj') @common
if ($LASTEXITCODE -ne 0) { throw 'Building the add-in failed.' }
$addInOut = (& dotnet msbuild (Join-Path $repo 'src\SwVault.AddIn\SwVault.AddIn.csproj') -getProperty:TargetDir -p:Configuration=Release).Trim()
Copy-Item (Join-Path $addInOut '*') (Join-Path $stage 'addin') -Recurse -Force

if ($TeamConfig) {
    $team = Get-Content $TeamConfig -Raw | ConvertFrom-Json
    if (-not $team.vaultUrl) { throw "$TeamConfig has no vaultUrl." }
    Copy-Item $TeamConfig (Join-Path $stage 'bin\team.json')
    Write-Host "Team: $($team.name) -> $($team.vaultUrl)"
}

if ($MinGitUrl -ne 'none') {
    if (-not $MinGitUrl) {
        $release = Invoke-RestMethod -Uri 'https://api.github.com/repos/git-for-windows/git/releases/latest' -Headers @{ 'User-Agent' = 'SwVault-package' }
        $asset = $release.assets | Where-Object { $_.name -match '^MinGit-[\d.]+-64-bit\.zip$' } | Select-Object -First 1
        if (-not $asset) { throw "No 64-bit MinGit asset in Git for Windows $($release.tag_name); pass -MinGitUrl." }
        $MinGitUrl = $asset.browser_download_url
    }
    $cache = Join-Path $repo 'dist\cache'
    New-Item -ItemType Directory -Force -Path $cache | Out-Null
    $minGitZip = Join-Path $cache (Split-Path $MinGitUrl -Leaf)
    if (-not (Test-Path $minGitZip)) {
        Write-Host "Downloading $MinGitUrl"
        Invoke-WebRequest -Uri $MinGitUrl -OutFile $minGitZip -UseBasicParsing
    }
    Expand-Archive -Path $minGitZip -DestinationPath (Join-Path $stage 'bin\git') -Force
    if (-not (Test-Path (Join-Path $stage 'bin\git\cmd\git.exe'))) { throw "$minGitZip doesn't look like MinGit (no cmd\git.exe)." }
}

Copy-Item (Join-Path $PSScriptRoot 'install\install.ps1') $stage
Copy-Item (Join-Path $PSScriptRoot 'install\Install SwVault.cmd') $stage
Copy-Item (Join-Path $PSScriptRoot 'install\uninstall.ps1') $stage
Copy-Item (Join-Path $PSScriptRoot 'install\INSTALL.txt') $stage

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
Write-Host "Package: $zip ($([math]::Round((Get-Item $zip).Length / 1MB)) MB)"

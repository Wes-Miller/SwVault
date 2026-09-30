<#
.SYNOPSIS
  Builds a SwVault install package: dist\SwVault-<version>.zip

.DESCRIPTION
  Layout inside the zip:
    bin\      SwVault.Agent.exe and swvault.exe (self-contained .NET 10, no runtime to install)
    addin\    SwVault.AddIn.dll (+ SOLIDWORKS interop and protocol DLLs)
    install.ps1 / uninstall.ps1 / INSTALL.txt
  Teammates unzip it and run install.ps1 (administrator; one UAC prompt).
#>
[CmdletBinding()]
param([string]$Version = '0.1.0')

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$stage = Join-Path $repo "dist\SwVault-$Version"
$zip = Join-Path $repo "dist\SwVault-$Version.zip"
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

Copy-Item (Join-Path $PSScriptRoot 'install\install.ps1') $stage
Copy-Item (Join-Path $PSScriptRoot 'install\uninstall.ps1') $stage
Copy-Item (Join-Path $PSScriptRoot 'install\INSTALL.txt') $stage

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
Write-Host "Package: $zip ($([math]::Round((Get-Item $zip).Length / 1MB)) MB)"

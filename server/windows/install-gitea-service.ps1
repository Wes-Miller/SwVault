<#
.SYNOPSIS
  Installs Gitea as a Windows service on a team PC (the "shop PC" hosting option).

.DESCRIPTION
  - Puts gitea.exe and its data under -Root (use a big, backed-up drive, e.g. D:\SwVaultServer).
  - Writes app.ini with LFS + LFS locking on, registration off, sign-in required.
  - Registers a Windows service "SwVault-Gitea" that starts at boot.
  - Opens the firewall for the chosen port on the Domain/Private profiles only.

  HTTPS: put Caddy in front (see server\docker\Caddyfile; Caddy also runs on Windows), or
  terminate TLS with a certificate from campus IT. Plain HTTP is only acceptable for testing.

  Needs Git for Windows on the server (Gitea's hooks use its bash). Run as administrator.

.EXAMPLE
  .\install-gitea-service.ps1 -Root D:\SwVaultServer -GiteaExe .\gitea-1.27.3-windows-4.0-amd64.exe -Hostname vault.shop.example.edu
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Root,
    [Parameter(Mandatory)] [string]$GiteaExe,
    [Parameter(Mandatory)] [string]$Hostname,
    [int]$Port = 3000,
    [string]$RootUrl
)

$ErrorActionPreference = 'Stop'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script as administrator.'
}
$gitCmd = (Get-Command git -ErrorAction SilentlyContinue).Source
if (-not $gitCmd) { throw 'Install Git for Windows on the server first (https://git-scm.com/download/win).' }
$gitRoot = Split-Path (Split-Path $gitCmd -Parent) -Parent
if (-not $RootUrl) { $RootUrl = "https://$Hostname/" }

$data = Join-Path $Root 'data'
$exe = Join-Path $Root 'gitea.exe'
$ini = Join-Path $Root 'app.ini'
New-Item -ItemType Directory -Force -Path $data | Out-Null
Copy-Item $GiteaExe $exe -Force

function Secret([string]$Kind) { (& $exe generate secret $Kind).Trim() }

if (-not (Test-Path $ini)) {
    $fwd = $data -replace '\\', '/'
    @"
APP_NAME = SwVault
RUN_MODE = prod
WORK_PATH = $fwd

[server]
PROTOCOL = http
HTTP_ADDR = 0.0.0.0
HTTP_PORT = $Port
DOMAIN = $Hostname
ROOT_URL = $RootUrl
DISABLE_SSH = true
LFS_START_SERVER = true
LFS_JWT_SECRET = $(Secret 'JWT_SECRET')
LFS_MAX_FILE_SIZE = 4294967296

[database]
DB_TYPE = sqlite3
PATH = $fwd/gitea.db

[repository]
ROOT = $fwd/repos
DEFAULT_BRANCH = main
SCRIPT_TYPE = bash

[lfs]
PATH = $fwd/lfs

[security]
INSTALL_LOCK = true
SECRET_KEY = $(Secret 'SECRET_KEY')
INTERNAL_TOKEN = $(Secret 'INTERNAL_TOKEN')

[oauth2]
ENABLED = true
JWT_SECRET = $(Secret 'JWT_SECRET')

[service]
DISABLE_REGISTRATION = true
REQUIRE_SIGNIN_VIEW = true

[log]
MODE = file
LEVEL = Warn
ROOT_PATH = $fwd/log
"@ | Set-Content -Path $ini -Encoding ASCII
}

& $exe --config $ini --work-path $data migrate
if ($LASTEXITCODE -ne 0) { throw 'gitea migrate failed.' }

# The service needs Git's bash on PATH for Gitea's server-side hooks.
$machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
foreach ($dir in "$gitRoot\bin", "$gitRoot\usr\bin") {
    if ($machinePath -notlike "*$dir*") { $machinePath = "$dir;$machinePath" }
}
[Environment]::SetEnvironmentVariable('Path', $machinePath, 'Machine')

if (-not (Get-Service SwVault-Gitea -ErrorAction SilentlyContinue)) {
    & sc.exe create SwVault-Gitea start= auto binPath= "`"$exe`" web --config `"$ini`" --work-path `"$data`"" DisplayName= "SwVault vault server (Gitea)" | Out-Null
    & sc.exe failure SwVault-Gitea reset= 86400 actions= restart/5000/restart/30000/restart/60000 | Out-Null
}
Start-Service SwVault-Gitea

if (-not (Get-NetFirewallRule -DisplayName 'SwVault Gitea' -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName 'SwVault Gitea' -Direction Inbound -Protocol TCP -LocalPort $Port -Profile Domain, Private -Action Allow | Out-Null
}

Write-Host "Gitea is running on port $Port. Next steps (server\README.md):"
Write-Host "  1. Create the first admin:  & '$exe' --config '$ini' --work-path '$data' admin user create --admin --username <you> --email <you@...> --password <temp> --must-change-password"
Write-Host "  2. Put HTTPS in front (Caddy) and point $Hostname at this PC."
Write-Host "  3. Schedule server\backup\backup-gitea.ps1 nightly."

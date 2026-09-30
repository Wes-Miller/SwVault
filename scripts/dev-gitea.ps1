<#
.SYNOPSIS
  Runs a local Gitea (SQLite, localhost only) for SwVault development and end-to-end tests.

.DESCRIPTION
  -Setup   writes app.ini, migrates the database, creates an admin plus test users (alice, bob, carol),
           issues access tokens, and creates the org "fsae" with a private repo "vault".
           Credentials are written to <Root>\dev-users.json (local test values only).
  -Start   starts gitea.exe in the background.
  -Stop    stops it.
  -Status  shows whether it is running.

  Download gitea.exe first (https://dl.gitea.com/gitea/) to <Root>\gitea.exe.

.EXAMPLE
  .\scripts\dev-gitea.ps1 -Setup
  .\scripts\dev-gitea.ps1 -Start
#>
[CmdletBinding()]
param(
    [switch]$Setup,
    [switch]$Start,
    [switch]$Stop,
    [switch]$Status,
    [string]$Root = "C:\SwVaultDev\gitea",
    [int]$Port = 3000
)

$ErrorActionPreference = 'Stop'
$exe = Join-Path $Root 'gitea.exe'
$data = Join-Path $Root 'data'
$ini = Join-Path $Root 'app.ini'
$usersFile = Join-Path $Root 'dev-users.json'
$pidFile = Join-Path $Root 'gitea.pid'
# 127.0.0.1, not localhost: gitea listens on IPv4 only, and .NET tries ::1 first (about 2 s per new connection).
$baseUrl = "http://127.0.0.1:$Port"

function New-RandomString([int]$Length = 24) {
    $chars = 'abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789'.ToCharArray()
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $bytes = New-Object byte[] $Length
    $rng.GetBytes($bytes)
    -join ($bytes | ForEach-Object { $chars[$_ % $chars.Length] })
}

# Gitea's server-side git hooks need bash; Git for Windows ships one that isn't on PATH by default.
$gitCmd = (Get-Command git -ErrorAction SilentlyContinue).Source
if ($gitCmd) {
    $gitRoot = Split-Path (Split-Path $gitCmd -Parent) -Parent
    $env:Path = "$gitRoot\bin;$gitRoot\usr\bin;$env:Path"
}

# Runs gitea.exe and returns stdout. Uses Start-Process so gitea's stderr warnings don't
# become terminating errors in Windows PowerShell.
function Invoke-GiteaExe([string[]]$Arguments, [switch]$AllowFailure) {
    $out = [System.IO.Path]::GetTempFileName()
    $err = [System.IO.Path]::GetTempFileName()
    try {
        $quoted = $Arguments | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } }
        $p = Start-Process -FilePath $exe -ArgumentList $quoted -NoNewWindow -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
        $null = $p.Handle # Windows PowerShell only reports ExitCode if the handle was opened before exit
        $p.WaitForExit()
        $stdout = "$(Get-Content $out -Raw)"
        $stderr = "$(Get-Content $err -Raw)"
        if ($p.ExitCode -ne 0 -and -not $AllowFailure) { throw "gitea $($Arguments -join ' ') failed: $stderr $stdout" }
        return $stdout.Trim()
    } finally {
        Remove-Item $out, $err -ErrorAction SilentlyContinue
    }
}

function Invoke-Gitea([string[]]$Arguments, [switch]$AllowFailure) {
    Invoke-GiteaExe (@('--config', $ini, '--work-path', $data) + $Arguments) -AllowFailure:$AllowFailure
}

function Get-GiteaProcess {
    if (-not (Test-Path $pidFile)) { return $null }
    $id = Get-Content $pidFile -Raw
    return Get-Process -Id ([int]$id) -ErrorAction SilentlyContinue
}

function Wait-Gitea {
    # 127.0.0.1 rather than localhost: gitea listens on IPv4 only.
    $deadline = (Get-Date).AddMinutes(3)
    while ((Get-Date) -lt $deadline) {
        try {
            $r = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/api/healthz" -UseBasicParsing -TimeoutSec 5
            if ($r.StatusCode -eq 200) { return }
        } catch { }
        Start-Sleep -Seconds 1
    }
    throw "Gitea did not become healthy at $baseUrl (see $data\log\gitea.log)"
}

function Start-Gitea {
    if (Get-GiteaProcess) { Write-Host "Gitea already running at $baseUrl"; return }
    $p = Start-Process -FilePath $exe -ArgumentList @('--config', "`"$ini`"", '--work-path', "`"$data`"", 'web') -WindowStyle Hidden -PassThru
    Set-Content -Path $pidFile -Value $p.Id
    Wait-Gitea
    Write-Host "Gitea running at $baseUrl (pid $($p.Id))"
}

function New-GiteaSecret([string]$Kind) {
    return Invoke-GiteaExe @('generate', 'secret', $Kind)
}

function Invoke-Api([string]$Method, [string]$Path, [string]$Token, $Body) {
    $headers = @{ Authorization = "token $Token" }
    $request = @{ Method = $Method; Uri = "http://127.0.0.1:$Port/api/v1/$Path"; Headers = $headers; UseBasicParsing = $true; ContentType = 'application/json' }
    if ($null -ne $Body) { $request.Body = ($Body | ConvertTo-Json -Depth 5) }
    try {
        return Invoke-RestMethod @request
    } catch {
        $code = $_.Exception.Response.StatusCode.value__
        if ($code -eq 409 -or $code -eq 422) { return $null } # already exists
        throw
    }
}

if (-not ($Setup -or $Start -or $Stop -or $Status)) { $Status = $true }
if (-not (Test-Path $exe)) { throw "gitea.exe not found at $exe. Download it from https://dl.gitea.com/gitea/ first." }

if ($Stop) {
    $p = Get-GiteaProcess
    if ($p) { Stop-Process -Id $p.Id -Force; Write-Host "Stopped Gitea (pid $($p.Id))" } else { Write-Host "Gitea is not running" }
    Remove-Item $pidFile -ErrorAction SilentlyContinue
}

if ($Setup) {
    New-Item -ItemType Directory -Force -Path $data | Out-Null
    if (-not (Test-Path $ini)) {
        $dataFwd = $data -replace '\\', '/'
        $secretKey = New-GiteaSecret 'SECRET_KEY'
        $internalToken = New-GiteaSecret 'INTERNAL_TOKEN'
        $lfsSecret = New-GiteaSecret 'JWT_SECRET'
        $oauthSecret = New-GiteaSecret 'JWT_SECRET'
        @"
APP_NAME = SwVault Dev
RUN_MODE = prod
WORK_PATH = $dataFwd

[server]
PROTOCOL = http
HTTP_ADDR = 127.0.0.1
HTTP_PORT = $Port
DOMAIN = 127.0.0.1
ROOT_URL = $baseUrl/
DISABLE_SSH = true
OFFLINE_MODE = true
LFS_START_SERVER = true
LFS_JWT_SECRET = $lfsSecret

[database]
DB_TYPE = sqlite3
PATH = $dataFwd/gitea.db

[repository]
ROOT = $dataFwd/repos
DEFAULT_BRANCH = main

[lfs]
PATH = $dataFwd/lfs

[security]
INSTALL_LOCK = true
SECRET_KEY = $secretKey
INTERNAL_TOKEN = $internalToken

[oauth2]
JWT_SECRET = $oauthSecret

[service]
DISABLE_REGISTRATION = true
REQUIRE_SIGNIN_VIEW = true

[log]
MODE = file
LEVEL = Warn
ROOT_PATH = $dataFwd/log
"@ | Set-Content -Path $ini -Encoding ASCII
        Write-Host "Wrote $ini"
    }

    Invoke-Gitea @('migrate') | Out-Null

    $existing = @{}
    if (Test-Path $usersFile) {
        $saved = Get-Content $usersFile -Raw | ConvertFrom-Json
        foreach ($u in $saved.users) { $existing[$u.user] = $u }
    }

    $users = @()
    foreach ($spec in @(@{ user = 'swadmin'; admin = $true }, @{ user = 'alice' }, @{ user = 'bob' }, @{ user = 'carol' })) {
        $name = $spec.user
        if ($existing.ContainsKey($name)) { $users += $existing[$name]; continue }
        $password = New-RandomString 20
        $create = @('admin', 'user', 'create', '--username', $name, '--password', $password, '--email', "$name@example.com", '--must-change-password=false')
        if ($spec.admin) { $create += '--admin' }
        Invoke-Gitea $create -AllowFailure | Out-Null
        # If the user already existed (an earlier interrupted setup), make the saved password true.
        Invoke-Gitea @('admin', 'user', 'change-password', '--username', $name, '--password', $password, '--must-change-password=false') | Out-Null
        $token = Invoke-Gitea @('admin', 'user', 'generate-access-token', '--username', $name, '--token-name', ("swvault-dev-" + (Get-Date -Format 'yyyyMMddHHmmss')), '--scopes', 'all', '--raw')
        $users += [pscustomobject]@{ user = $name; password = $password; token = $token.Trim(); admin = [bool]$spec.admin }
    }

    Start-Gitea
    $adminToken = ($users | Where-Object { $_.user -eq 'swadmin' }).token
    Invoke-Api POST 'orgs' $adminToken @{ username = 'fsae'; visibility = 'private' } | Out-Null
    Invoke-Api POST 'orgs/fsae/repos' $adminToken @{ name = 'vault'; private = $true; default_branch = 'main' } | Out-Null
    Invoke-Api PUT 'repos/fsae/vault/collaborators/alice' $adminToken @{ permission = 'admin' } | Out-Null
    Invoke-Api PUT 'repos/fsae/vault/collaborators/bob' $adminToken @{ permission = 'write' } | Out-Null
    Invoke-Api PUT 'repos/fsae/vault/collaborators/carol' $adminToken @{ permission = 'write' } | Out-Null

    [pscustomobject]@{ baseUrl = $baseUrl; vaultUrl = "$baseUrl/fsae/vault.git"; users = $users } |
        ConvertTo-Json -Depth 4 | Set-Content -Path $usersFile -Encoding UTF8
    Write-Host "Setup complete. Test credentials (local only) saved to $usersFile"
    Write-Host "Vault repository: $baseUrl/fsae/vault.git"
}

if ($Start) { Start-Gitea }

if ($Status) {
    $p = Get-GiteaProcess
    if ($p) { Write-Host "Gitea running at $baseUrl (pid $($p.Id))" } else { Write-Host "Gitea is not running" }
}

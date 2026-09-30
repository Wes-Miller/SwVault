#!/usr/bin/env bash
# One-time setup of the SwVault vault server on a Linux PC (Ubuntu/Debian/Fedora/Raspberry Pi OS,
# 64-bit). Safe to run again: it keeps existing data and only fills in what's missing.
#
#   sudo ./setup.sh
#
# What it does:
#   1. Installs Docker, jq and curl if needed.
#   2. Starts Tailscale (Funnel) + Gitea with docker compose. The server gets a public HTTPS
#      address like https://swvault.tail1234.ts.net that works from anywhere, using outbound
#      connections only, so the network it sits on doesn't matter (apartment, CGNAT, no
#      port forwarding).
#   3. Creates your admin account, the team organization, the Designers/Viewers teams and the
#      vault repository.
#   4. Writes team.json. Build the team's SwVault installer with it on a Windows PC:
#        .\scripts\package.ps1 -TeamConfig team.json
#      Members install that and sign in; nothing else to configure.
#   5. Schedules nightly backups and a health watchdog, and stops the PC from sleeping.
#
# Before you start (5 minutes, once): make a free Tailscale account at https://login.tailscale.com
# and create an auth key (Settings > Keys > Generate auth key; not reusable, not ephemeral).
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

[[ $EUID -eq 0 ]] || die "Run as root: sudo $0"
[[ "$(uname -s)" == Linux ]] || die "This script is for Linux."

# ------------------------------------------------------------------ 1. prerequisites
install_packages() {
    if command -v apt-get >/dev/null; then
        apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq "$@"
    elif command -v dnf >/dev/null; then
        dnf install -y -q "$@"
    else
        die "Install these packages yourself, then run setup again: $*"
    fi
}

say "Checking prerequisites"
missing=()
for tool in curl jq rsync; do command -v "$tool" >/dev/null || missing+=("$tool"); done
((${#missing[@]} == 0)) || install_packages "${missing[@]}"
if ! command -v docker >/dev/null || ! docker compose version >/dev/null 2>&1; then
    say "Installing Docker (get.docker.com)"
    curl -fsSL https://get.docker.com | sh
fi
systemctl enable --now docker >/dev/null
[[ -e /dev/net/tun ]] || die "/dev/net/tun is missing (needed by Tailscale). On a VM/LXC container, enable TUN for it."

# ------------------------------------------------------------------ 2. settings
ask() {
    # ask KEY "Question" default   -> keeps the value from .env on re-runs
    local key="$1" question="$2" default="${3:-}" current answer
    current="$(env_get "$key")"
    [[ -n "$current" ]] && { printf '%s\n' "$current"; return; }
    read -r -p "$question [${default}]: " answer </dev/tty
    printf '%s\n' "${answer:-$default}"
}

echo
say "Team settings (press Enter to accept the [default])"
TEAM_NAME="$(ask TEAM_NAME 'Team name, shown in SwVault' 'FSAE')"
default_org="$(printf '%s' "$TEAM_NAME" | tr '[:upper:]' '[:lower:]' | tr -cs 'a-z0-9' '-' | sed 's/^-*//; s/-*$//')"
ORG="$(ask ORG 'Organization name on the server (lowercase, no spaces)' "${default_org:-team}")"
REPO="$(ask REPO 'Vault repository name' 'cad')"
ADMIN_USER="$(ask ADMIN_USER 'Your admin user name' "${SUDO_USER:-admin}")"
LOCAL_ROOT="$(ask LOCAL_ROOT 'Vault folder on every Windows PC' "C:\\SWVault\\$TEAM_NAME")"
SW_VERSION="$(ask SW_VERSION 'SOLIDWORKS version everyone uses (e.g. 2025; empty = no check)' '')"
TS_HOSTNAME="$(ask TS_HOSTNAME 'Server name (becomes https://<name>.<tailnet>.ts.net)' 'swvault')"
BACKUP_DIR="$(ask BACKUP_DIR 'Backup folder (best on a second disk or USB drive)' '/var/backups/swvault')"
for kv in TEAM_NAME ORG REPO ADMIN_USER LOCAL_ROOT SW_VERSION TS_HOSTNAME BACKUP_DIR; do env_set "$kv" "${!kv}"; done
[[ "$ORG" =~ ^[a-z0-9][a-z0-9._-]*$ ]] || die "Organization name '$ORG' must be lowercase letters, digits, - . _"
[[ "$ADMIN_USER" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]] || die "User name '$ADMIN_USER' may only contain letters, digits, - . _"

# Gitea secrets, generated once (setup never shows the web installer).
secret() { head -c 32 /dev/urandom | base64 | tr '+/' '-_' | tr -d '=\n'; }
for key in GITEA_SECRET_KEY GITEA_INTERNAL_TOKEN GITEA_LFS_JWT_SECRET GITEA_OAUTH_JWT_SECRET; do
    [[ -n "$(env_get "$key")" ]] || env_set "$key" "$(secret)"
done

mkdir -p "$DATA_DIR/tailscale" "$DATA_DIR/gitea"
chown 1000:1000 "$DATA_DIR/gitea"
chmod 700 "$DATA_DIR"

# ------------------------------------------------------------------ 3. Tailscale
say "Starting Tailscale"
if [[ ! -e "$DATA_DIR/tailscale/tailscaled.state" ]]; then
    echo "Paste a Tailscale auth key (https://login.tailscale.com/admin/settings/keys)."
    read -r -s -p "Auth key: " ts_key </dev/tty
    echo
    [[ "$ts_key" == tskey-* ]] || die "That doesn't look like a Tailscale auth key (tskey-...)."
    # Only lives in .env until the node has joined (removed below).
    env_set TS_AUTHKEY "$ts_key"
fi
compose pull -q
compose up -d tailscale

started=$SECONDS
deadline=$((SECONDS + 180))
while :; do
    status="$(ts_status || true)"
    state="$(jq -r '.BackendState // empty' <<<"$status" 2>/dev/null || true)"
    [[ "$state" == Running ]] && break
    if [[ "$state" == NeedsMachineAuth ]]; then
        die "Tailscale is waiting for you to approve this device: https://login.tailscale.com/admin/machines (then run setup again)."
    fi
    if [[ "$state" == NeedsLogin ]] && (( SECONDS - started > 30 )); then
        env_set TS_AUTHKEY ""
        die "Tailscale rejected the auth key (expired or already used). Make a new one and run setup again."
    fi
    (( SECONDS < deadline )) || die "Tailscale didn't connect within 3 minutes (docker compose logs tailscale). Does this PC have internet access?"
    sleep 3
done
env_set TS_AUTHKEY ""

cert_domain="$(jq -r '.CertDomains[0] // empty' <<<"$status")"
if [[ -z "$cert_domain" ]]; then
    die "HTTPS certificates are off in your tailnet. Open https://login.tailscale.com/admin/dns, click 'Enable HTTPS', then run setup again."
fi
if ! jq -e '(.Self.CapMap // {} | has("funnel")) or ((.Self.Capabilities // []) | index("funnel"))' <<<"$status" >/dev/null; then
    warn "This node isn't allowed to use Funnel yet. In https://login.tailscale.com/admin/acls add:"
    warn '  "nodeAttrs": [{ "target": ["autogroup:member"], "attr": ["funnel"] }]'
    warn "then run setup again. (New tailnets usually have this already.)"
fi
env_set VAULT_HOSTNAME "$cert_domain"
say "Server address: https://$cert_domain"

# ------------------------------------------------------------------ 4. Gitea
say "Starting Gitea"
compose up -d
wait_gitea 300

if [[ ! -s "$ADMIN_TOKEN_FILE" ]]; then
    say "Creating admin account '$ADMIN_USER'"
    admin_password="$(gen_password)"
    if ! gitea_cli admin user create --admin --username "$ADMIN_USER" --password "$admin_password" \
            --email "$ADMIN_USER@noreply.localhost" --must-change-password=false >/dev/null 2>&1; then
        # Already exists (an earlier, interrupted setup): make the password known again.
        gitea_cli admin user change-password --username "$ADMIN_USER" --password "$admin_password" --must-change-password=false >/dev/null
    fi
    token="$(gitea_cli admin user generate-access-token --username "$ADMIN_USER" \
        --token-name "swvault-admin-$(date +%Y%m%d%H%M%S)" --scopes all --raw | tr -d '\r\n')"
    [[ -n "$token" ]] || die "Could not create the admin token."
    (umask 077; printf '%s' "$token" > "$ADMIN_TOKEN_FILE")
    (umask 077; printf 'SwVault admin sign-in\n  User name: %s\n  Password:  %s\n' "$ADMIN_USER" "$admin_password" > "$DATA_DIR/admin-credentials.txt")
fi

say "Creating organization '$ORG', teams and repository '$REPO'"
API_OK_CODES="409 422"
api POST orgs "$(jq -n --arg o "$ORG" --arg n "$TEAM_NAME" '{username: $o, full_name: $n, visibility: "private"}')" >/dev/null
for spec in "Designers:write:Can check files out and in" "Viewers:read:Can open and download files only"; do
    IFS=: read -r name perm description <<<"$spec"
    if [[ -z "$(team_id "$ORG" "$name")" ]]; then
        api POST "orgs/$ORG/teams" "$(jq -n --arg n "$name" --arg p "$perm" --arg d "$description" \
            '{name: $n, description: $d, permission: $p, includes_all_repositories: true, can_create_org_repo: false,
              units: ["repo.code"], units_map: {"repo.code": $p}}')" >/dev/null
    fi
done
api POST "orgs/$ORG/repos" "$(jq -n --arg r "$REPO" '{name: $r, private: true, default_branch: "main", auto_init: false}')" >/dev/null
# No force pushes or deleting main: the vault's history can't be rewritten, only added to.
if ! api GET "repos/$ORG/$REPO/branch_protections" | jq -e '.[] | select(.rule_name == "main")' >/dev/null; then
    api POST "repos/$ORG/$REPO/branch_protections" '{"rule_name": "main", "enable_push": true}' >/dev/null
fi
API_OK_CODES=""

write_team_json
chmod 644 "$TEAM_FILE"

# ------------------------------------------------------------------ 5. keep it running
say "Scheduling nightly backups and a health watchdog"
mkdir -p "$BACKUP_DIR"
chmod 700 "$BACKUP_DIR"
backup_script="$(cd "$SWVAULT_DIR/../backup" && pwd)/backup-gitea.sh"
chmod +x "$backup_script" "$SWVAULT_DIR/swvault-admin.sh" "$SWVAULT_DIR/setup.sh"
cat > /etc/systemd/system/swvault-backup.service <<EOF
[Unit]
Description=SwVault nightly backup
After=docker.service

[Service]
Type=oneshot
ExecStart=$backup_script $SWVAULT_DIR $BACKUP_DIR
EOF
cat > /etc/systemd/system/swvault-backup.timer <<EOF
[Unit]
Description=SwVault nightly backup

[Timer]
OnCalendar=*-*-* 03:15
Persistent=true
RandomizedDelaySec=10m

[Install]
WantedBy=timers.target
EOF
cat > /etc/systemd/system/swvault-watchdog.service <<EOF
[Unit]
Description=SwVault health watchdog (restarts the server if it stops answering)
After=docker.service

[Service]
Type=oneshot
ExecStart=$SWVAULT_DIR/swvault-admin.sh watchdog
EOF
cat > /etc/systemd/system/swvault-watchdog.timer <<EOF
[Unit]
Description=SwVault health watchdog

[Timer]
OnBootSec=5min
OnUnitActiveSec=5min

[Install]
WantedBy=timers.target
EOF
systemctl daemon-reload
systemctl enable --now swvault-backup.timer swvault-watchdog.timer >/dev/null

if [[ "$(env_get KEEP_AWAKE)" != no ]]; then
    read -r -p "Stop this PC from sleeping/suspending (recommended for a server)? [Y/n]: " awake </dev/tty
    if [[ "${awake:-y}" =~ ^[Yy] ]]; then
        systemctl mask sleep.target suspend.target hibernate.target hybrid-sleep.target >/dev/null 2>&1 || true
        if [[ -f /etc/systemd/logind.conf ]] && ! grep -q '^HandleLidSwitch=ignore' /etc/systemd/logind.conf; then
            printf '\n[Login]\nHandleLidSwitch=ignore\nHandleLidSwitchExternalPower=ignore\n' >> /etc/systemd/logind.conf
        fi
        env_set KEEP_AWAKE yes
    else
        env_set KEEP_AWAKE no
    fi
fi

# ------------------------------------------------------------------ 6. check from the internet
say "Checking https://$cert_domain from the internet (the public name can take a few minutes to appear)"
deadline=$((SECONDS + 600))
reachable=no
while (( SECONDS < deadline )); do
    if curl -fsS -m 15 "https://$cert_domain/api/healthz" >/dev/null 2>&1; then reachable=yes; break; fi
    sleep 15
done

echo
if [[ "$reachable" == yes ]]; then
    say "SwVault server is up at https://$cert_domain"
else
    warn "https://$cert_domain isn't reachable yet. Check Funnel is allowed (see the note above), then: ./swvault-admin.sh status"
fi
cat <<EOF

Your admin sign-in is in $DATA_DIR/admin-credentials.txt (only root can read it).

Next steps:
  1. Copy $TEAM_FILE to your Windows build PC and build the team installer:
       .\\scripts\\package.ps1 -TeamConfig team.json
  2. Install it on your own PC first and sign in as '$ADMIN_USER'. That sets up the vault
     (you become its admin and approver).
  3. Add members:  sudo ./swvault-admin.sh add-user <name>   (prints their password)
     and send them the installer zip.
  4. Import existing files from SOLIDWORKS: SwVault tab > Import Folder.

Backups go to $BACKUP_DIR every night. Copy them somewhere else now and then.
EOF

# Shared helpers for setup.sh and swvault-admin.sh. Source it; don't run it.
# shellcheck shell=bash

SWVAULT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ENV_FILE="$SWVAULT_DIR/.env"
DATA_DIR="$SWVAULT_DIR/data"
ADMIN_TOKEN_FILE="$DATA_DIR/admin-token"
TEAM_FILE="$SWVAULT_DIR/team.json"
LOCAL_API="http://127.0.0.1:3000/api/v1"

say() { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33mwarning:\033[0m %s\n' "$*" >&2; }
die() { printf '\033[1;31merror:\033[0m %s\n' "$*" >&2; exit 1; }

compose() { docker compose --project-directory "$SWVAULT_DIR" -f "$SWVAULT_DIR/docker-compose.yml" "$@"; }

# Reads KEY from .env (empty if missing).
env_get() {
    [[ -f "$ENV_FILE" ]] || return 0
    grep -E "^$1=" "$ENV_FILE" | tail -n 1 | cut -d= -f2- || true
}

# Sets KEY=VALUE in .env (values must not contain newlines).
env_set() {
    local key="$1" value="$2"
    touch "$ENV_FILE"
    chmod 600 "$ENV_FILE"
    if grep -qE "^$key=" "$ENV_FILE"; then
        local tmp
        tmp="$(mktemp)"
        # ENVIRON, not -v: awk -v would eat the backslashes in Windows paths.
        K="$key" V="$value" awk 'BEGIN { FS = "=" } $1 == ENVIRON["K"] { print ENVIRON["K"] "=" ENVIRON["V"]; next } { print }' "$ENV_FILE" > "$tmp"
        cat "$tmp" > "$ENV_FILE"
        rm -f "$tmp"
    else
        printf '%s=%s\n' "$key" "$value" >> "$ENV_FILE"
    fi
}

# Readable random password: 4 groups of 4 (no look-alike characters), e.g. "k7Qm-Rt3x-9bWe-hPz4".
gen_password() {
    local chars='abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789' out='' i byte
    for i in $(seq 1 16); do
        byte=$(od -An -N1 -tu1 /dev/urandom | tr -d ' ')
        out+="${chars:$((byte % ${#chars})):1}"
        if (( i % 4 == 0 && i < 16 )); then out+='-'; fi
    done
    printf '%s' "$out"
}

gitea_cli() { compose exec -T -u git gitea gitea "$@" </dev/null; }

wait_gitea() {
    local deadline=$((SECONDS + ${1:-180}))
    until curl -fsS -m 5 "http://127.0.0.1:3000/api/healthz" >/dev/null 2>&1; do
        (( SECONDS < deadline )) || die "Gitea didn't come up at http://127.0.0.1:3000 (see: docker compose logs gitea)."
        sleep 2
    done
}

admin_token() {
    [[ -r "$ADMIN_TOKEN_FILE" ]] || die "No admin token at $ADMIN_TOKEN_FILE. Run setup.sh first (as root)."
    cat "$ADMIN_TOKEN_FILE"
}

# api METHOD PATH [JSON]  -> prints the response body. Fails on HTTP errors except the codes in
# API_OK_CODES (space-separated, e.g. "409 422" for "already exists").
API_OK_CODES=""
api() {
    local method="$1" path="$2" body="${3:-}" out code
    out="$(mktemp)"
    local args=(-sS -o "$out" -w '%{http_code}' -X "$method" -H "Authorization: token $(admin_token)" -H 'Content-Type: application/json' -H 'Accept: application/json')
    [[ -n "$body" ]] && args+=(--data "$body")
    code="$(curl "${args[@]}" "$LOCAL_API/$path")" || { rm -f "$out"; die "Can't reach Gitea at $LOCAL_API (is it running? swvault-admin.sh status)."; }
    if (( code >= 400 )) && [[ " $API_OK_CODES " != *" $code "* ]]; then
        local msg
        msg="$(cat "$out")"
        rm -f "$out"
        die "$method $path failed (HTTP $code): $msg"
    fi
    cat "$out"
    rm -f "$out"
}

team_id() {
    local org="$1" name="$2"
    api GET "orgs/$org/teams?limit=50" | jq -r --arg n "$name" '.[] | select(.name == $n) | .id' | head -n 1
}

# The Tailscale node's status (JSON), from inside the container.
ts_status() { compose exec -T tailscale tailscale status --json 2>/dev/null; }

vault_url() { printf 'https://%s/%s/%s.git' "$(env_get VAULT_HOSTNAME)" "$(env_get ORG)" "$(env_get REPO)"; }

write_team_json() {
    jq -n \
        --arg name "$(env_get TEAM_NAME)" \
        --arg url "$(vault_url)" \
        --arg root "$(env_get LOCAL_ROOT)" \
        --arg sw "$(env_get SW_VERSION)" \
        --arg admin "$(env_get ADMIN_USER)" \
        '{name: $name, vaultUrl: $url, localRoot: $root, admin: $admin, downloadAllOnJoin: true}
         + (if $sw == "" then {} else {solidworksVersion: $sw} end)' > "$TEAM_FILE"
}

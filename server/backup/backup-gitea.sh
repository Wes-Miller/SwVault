#!/usr/bin/env bash
# Nightly vault backup for the Docker deployments (server/docker and server/linux; the latter
# schedules it with a systemd timer). Run from cron as root, e.g.:
#   15 2 * * *  /opt/swvault/server/backup/backup-gitea.sh /opt/swvault/server/docker /mnt/backup
#
# Keeps 14 daily archives of the database/config/repositories, and mirrors the LFS content
# store (the big part) with rsync, which only copies new objects. Copy /mnt/backup offsite too.
set -euo pipefail

COMPOSE_DIR="${1:?compose directory}"
DEST="${2:?backup destination}"
STAMP="$(date +%Y%m%d-%H%M%S)"
mkdir -p "$DEST/daily" "$DEST/lfs"

cd "$COMPOSE_DIR"
# gitea dump: database, config, repositories (small: the vault repos only hold pointers + metadata).
docker compose exec -T -u git gitea bash -c "cd /tmp && gitea dump --skip-lfs-data --type tar.gz --file /tmp/swvault-dump.tar.gz"
docker compose cp gitea:/tmp/swvault-dump.tar.gz "$DEST/daily/gitea-$STAMP.tar.gz"
docker compose exec -T -u git gitea rm -f /tmp/swvault-dump.tar.gz

# Server settings and identity (server/linux layout): .env with Gitea secrets, Tailscale node
# state (keeps the same https://... address after a restore), admin token, team.json. Small.
config=()
# data/invites/*.json: invites, member/lead profiles, cars/subsystems and their REs, which review emails went out (not the installer zip).
for f in .env team.json data/admin-token data/admin-credentials.txt data/tailscale data/invites/invites.json data/invites/people.json data/invites/review-emails.json data/invites/subsystems.json; do
  if [[ -e "$COMPOSE_DIR/$f" ]]; then config+=("$f"); fi
done
if ((${#config[@]} > 0)); then
  (umask 077; tar -czf "$DEST/daily/server-config-$STAMP.tar.gz" -C "$COMPOSE_DIR" "${config[@]}")
fi

# LFS objects are content-addressed and never change, so an additive rsync is a complete backup.
rsync -a "$COMPOSE_DIR/data/gitea/git/lfs/" "$DEST/lfs/"

ls -1t "$DEST/daily"/gitea-*.tar.gz | tail -n +15 | xargs -r rm -f
ls -1t "$DEST/daily"/server-config-*.tar.gz 2>/dev/null | tail -n +15 | xargs -r rm -f
echo "Backup $STAMP complete."

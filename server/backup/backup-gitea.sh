#!/usr/bin/env bash
# Nightly vault backup for the Docker deployment. Run from cron as root, e.g.:
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

# LFS objects are content-addressed and never change, so an additive rsync is a complete backup.
rsync -a "$COMPOSE_DIR/data/gitea/git/lfs/" "$DEST/lfs/"

ls -1t "$DEST/daily"/gitea-*.tar.gz | tail -n +15 | xargs -r rm -f
echo "Backup $STAMP complete."

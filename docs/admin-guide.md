# SwVault admin guide

This guide is for the one or two people who run the vault. It's written so the next admin can take over when you graduate.

## 1. The server (Gitea)

SwVault works with any Git LFS server that supports **LFS file locking**. We use self-hosted **Gitea**: free, one binary or container, SQLite built in. Pick one hosting option:

| Option | Cost | Notes |
|---|---|---|
| CU OIT Container Platform | free today | Needs a faculty/staff sponsor for the namespace. Ask OIT about storage size, upload body-size limits (LFS needs large uploads) and future fees. Use `server/kubernetes/values.yaml`. |
| Team PC in the shop | one drive | Runs Gitea as a Windows service (`server/windows/install-gitea-service.ps1`). Off-campus members use the CU VPN. Ask CEAS IT/OIT whether VPN clients can reach the PC. |
| Linux box / VM with Docker | varies | `server/docker/docker-compose.yml` (Gitea + Caddy with automatic HTTPS). |

Gitea on Linux handles check-ins noticeably faster than on Windows: each push runs Gitea's hooks, and starting processes is slow on Windows (about 3–5 s per check-in vs well under 1 s).

**HTTPS:**
- Public hostname: Caddy gets a certificate automatically.
- Campus-only server: use Caddy's internal CA (`CADDY_TLS=internal`) and install its root certificate on team PCs.

Don't run the real vault over plain HTTP: access tokens travel with every request.

**Settings that matter** (already set in the provided configs):
- `LFS_START_SERVER = true`: file content and locks.
- `LFS_MAX_FILE_SIZE = 4294967296`: 4 GB per file.
- `DISABLE_REGISTRATION = true` and `REQUIRE_SIGNIN_VIEW = true`: accounts are created by you; nothing is public.
- `[oauth2] ENABLED = true`: lets Git Credential Manager do browser sign-in.

## 2. Users and permissions

1. Create an account per member (Site Administration → User Accounts), or add a login source (for example GitHub OAuth) so people sign in with an existing account.
2. Create an organization (for example `fsae`) with teams:

   | Team | Repository permission | Who |
   |---|---|---|
   | Designers | Write | Can check out and check in |
   | Viewers | Read | Can only open and get files |
   | Admins | Admin | Can force-release check-outs and change vault settings |

3. Members create an **access token** (Settings → Applications → Generate token, scopes: repository read/write, user read) and paste it into SwVault once. It's stored in Windows Credential Manager.

## 3. Creating a vault

Create an empty private repository (for example `fsae/cad`), then initialize it once from any PC with the CLI:

```powershell
swvault vault init https://vault.example.edu/fsae/cad.git --name "FSAE 2027" --root C:\SWVault\FSAE --sw-version 2025 --user <you> --token <token>
```

- `--root` is the folder **every** PC will use. SOLIDWORKS stores absolute reference paths, so the path must be the same everywhere.
- `--sw-version` blocks check-ins from a newer SOLIDWORKS release. A newer release would silently upgrade shared files that teammates on the older release then can't open.
- You become `admin` and `approver`.

On the server, protect `main`: Repository → Settings → Branches → add a rule for `main`, and block force push and deletion.

**Vault settings** live in `.swvault/vault.json` in the repository:

| Setting | What it controls |
|---|---|
| `roles` | Who is `admin` / `approver`, as server user names |
| `workflow` | States, transitions, which states are read-only, revision scheme and skipped letters |
| `releaseExportFolder` | Where release PDFs/STEPs go (default `{folder}/_Released`) |
| `ignore` | File patterns never checked in (SOLIDWORKS lock files `~$*`, backups, etc.) |
| `externalReferenceAllowList` | Folders outside the vault that references may point to without warnings (for example Toolbox) |

Edit it in the web UI, or check out and edit a clone. Changes apply to everyone within about a minute.

## 4. Importing existing files

In SOLIDWORKS: SwVault tab → **Import Folder**.
1. Pick the old folder (for example the team share) and a destination folder inside the vault.
2. **Scan.** Read the report: missing references, references outside the folder, duplicate file names, long paths.
3. **Import.** Files are copied, assembly and drawing references are re-pointed at the copies, and everything is checked in as batched commits.

If the import is interrupted, run it again: files already imported are skipped.

## 5. Everyday admin tasks

| Task | How |
|---|---|
| Someone left files checked out | `swvault unlock "<path>"` (admins only), or ask them to Undo Check Out |
| See all check-outs | `swvault locks` |
| Onboard a member | Account + team → they install SwVault → tray icon → Vaults... → paste URL, user name, token → Get Latest in the task pane |
| Check a PC's setup | `swvault doctor` |
| Logs | `%LOCALAPPDATA%\SwVault\logs` (agent and add-in) |

## 6. Backups (do this before the pilot)

- **Nightly:** `server/backup/backup-gitea.sh` (Docker) or `server/backup/backup-gitea.ps1` (Windows service) writes:
  - a small daily `gitea dump` (database, config, repositories), keeping 14;
  - a mirror of the LFS content store, the big part, which is append-only.

  Copy the backup folder to a second place regularly.
- **Restore drill** (once a semester, on a spare PC or VM):
  1. Install Gitea the same way.
  2. Restore the dump following Gitea's docs (`gitea restore`, or unpack the dump into the data folder).
  3. Copy the LFS mirror into `data/lfs`.
  4. Point a test PC at it and Get Latest on an assembly.

## 7. Storage growth

Every checked-in version is kept. Watch disk usage (Site Administration → Dashboard shows repository and LFS sizes). A season of active FSAE design is typically tens to a few hundred GB. When a season ends, you can start a fresh vault with only the current designs (Import Folder) and archive the old one.

## 8. Things SwVault can't stop

Locks and workflow are enforced by the SwVault client. Someone with write access could still push with plain `git` and bypass them. The git history records who did what, and branch protection stops history from being rewritten. Keep write access to the team.

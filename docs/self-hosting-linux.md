# Self-hosting SwVault on a Linux PC

This sets up the vault server on any 64-bit Linux PC: an old laptop, a mini PC or a Raspberry Pi 4/5 with a big drive. It works on a network you don't control, like an apartment complex, a dorm or a mobile hotspot:

- **No port forwarding, static IP or router access.** The server only makes outbound connections.
- **A real HTTPS address that works from anywhere,** like `https://swvault.tail1234.ts.net`. It comes from [Tailscale Funnel](https://tailscale.com/kb/1223/funnel), which is free for this use. Only the server runs Tailscale; team members don't install it.
- **Team members install one package and sign in.** Their files then download by themselves.

## How it fits together

```
Team PCs (Windows + SOLIDWORKS)                 Linux PC (anywhere with internet)
+----------------------------+    HTTPS    +---------------------+    +---------------------------+
| SwVault add-in + agent     | ----------> | Tailscale Funnel    | -> | tailscale container       |
| (package with team.json)   |  *.ts.net   | (public relay)      |    |  + gitea container        |
+----------------------------+             +---------------------+    |  data/ : repos, LFS files |
                                                                        +---------------------------+
```

- The `tailscale` container keeps an outbound connection to Tailscale. Funnel relays public HTTPS traffic for your `*.ts.net` name through it. TLS is terminated on your PC, with a Let's Encrypt certificate that Tailscale manages.
- The `gitea` container shares that container's network and listens on port 3000. The admin scripts reach it on `127.0.0.1:3000`, and nothing is exposed on your apartment's LAN.
- Everything lives in `server/linux/data/`. Backups go to a folder you choose, every night.

## What you need

- A 64-bit Linux PC that stays on and connected to the internet. Wired is best, but Wi-Fi works. Ubuntu Server 24.04 LTS is the easiest choice, and Debian, Fedora and Raspberry Pi OS (64-bit) also work.
- Disk space: plan for 100–500 GB. Every version of every file is kept.
- Ideally a second drive or a USB disk for backups.
- A free Tailscale account: <https://login.tailscale.com>. Sign in with Google, Microsoft or GitHub.
- A Windows PC with the .NET 10 SDK, to build the team installer (the same PC you build SwVault on).

## 1. Get a Tailscale auth key (once, 5 minutes)

1. Sign in at <https://login.tailscale.com>.
2. **DNS** page: make sure **MagicDNS** is on, and click **Enable HTTPS**.
3. **Access controls** page: new tailnets already allow Funnel. If yours shows a policy without a `funnel` node attribute, add:
   ```json
   "nodeAttrs": [{ "target": ["autogroup:member"], "attr": ["funnel"] }]
   ```
4. **Settings → Keys → Generate auth key.** Leave "Reusable" and "Ephemeral" off, and copy the key (`tskey-auth-...`).

The key is used once. After that, the server's Tailscale identity lives in `data/tailscale`.

## 2. Run the setup script on the Linux PC

```bash
sudo git clone https://github.com/wes-miller/swvault.git /opt/swvault   # or copy the repo there
cd /opt/swvault/server/linux
sudo ./setup.sh
```

It asks for:

| Question | Example | Notes |
|---|---|---|
| Team name | `FSAE` | Shown in SwVault. |
| Organization name | `fsae` | Lowercase; part of the vault URL. |
| Vault repository | `cad` | |
| Your admin user name | `wes` | You become the vault's admin and approver. |
| Vault folder on every Windows PC | `C:\SWVault\FSAE` | Must be the same on all PCs (SOLIDWORKS stores absolute paths). |
| SOLIDWORKS version | `2025` | Blocks check-ins from a newer release. Leave empty to allow any. |
| Server name | `swvault` | The address becomes `https://swvault.<your-tailnet>.ts.net`. |
| Backup folder | `/mnt/usb/swvault-backup` | Ideally on a second disk. |
| Tailscale auth key | `tskey-auth-...` | From step 1. |

Then it:

- installs Docker, jq and curl;
- starts Tailscale and Gitea;
- creates your admin account, the organization, the **Designers** (read/write) and **Viewers** (read-only) teams, and the vault repository, with `main` protected against force-push;
- writes `team.json`;
- schedules nightly backups (03:15) and a 5-minute health watchdog;
- offers to stop the PC from sleeping, including ignoring a closed laptop lid;
- checks that the address answers from the internet. A brand-new name can take a few minutes to appear in public DNS.

Your admin password is in `data/admin-credentials.txt`, readable by root only. Running `setup.sh` again is safe: it keeps your data and fills in anything missing.

## 3. Build the team installer (Windows)

Copy `server/linux/team.json` to your Windows build PC and run:

```powershell
.\scripts\package.ps1 -Version 0.2.0 -TeamConfig .\team.json
```

This produces `dist\SwVault-FSAE-0.2.0.zip`, containing:

- SwVault (agent, CLI and SOLIDWORKS add-in);
- **MinGit**, a portable Git that the script downloads, so members don't install Git;
- `team.json`, so the installer knows which vault to join.

Put the zip somewhere the team can download it, such as a shared drive or Teams.

## 4. Sign in once yourself

Install the zip on your own PC (double-click **Install SwVault.cmd**) and sign in with your admin user name and password. Because the vault is new, your first sign-in sets it up: SwVault writes `.swvault/vault.json` with you as admin and approver.

## 5. Add your team

```bash
sudo ./swvault-admin.sh add-user alice --name "Alice Smith"   # designer: check out / check in
sudo ./swvault-admin.sh add-user bob --viewer                 # read-only
sudo ./swvault-admin.sh add-user carol --approver             # designer who can release files
```

Each command prints a short card with the user name, a generated password and three install steps. Send it to the person privately, along with the installer zip.

For the member, that's all:

1. Double-click **Install SwVault.cmd** and approve the prompt.
2. Sign in when the SwVault window appears.
3. The files download to `C:\SWVault\FSAE`. A tray notification says when they're all there.
4. Open SOLIDWORKS: the SwVault tab and task pane are ready.

## Everyday admin

| Task | Command |
|---|---|
| Is everything up and reachable? | `sudo ./swvault-admin.sh status` |
| Add / list people | `add-user <name> [--viewer\|--approver\|--admin]`, `list-users` |
| Forgotten password | `reset-password <name>` |
| Someone leaves | `disable-user <name>`. They can't sign in, and their history is kept. Undo with `enable-user`. |
| Make someone admin or approver | `set-role <name> admin\|approver [--remove]` |
| Back up now | `backup` |
| Update Tailscale | `update` (Gitea is pinned; bump `GITEA_TAG` in `.env` after reading its release notes) |
| Logs | `logs gitea`, `logs tailscale` |

Run all of them with `sudo ./swvault-admin.sh` from `server/linux`. Also keep the Linux PC itself updated, for example with `sudo apt upgrade` monthly, or turn on `unattended-upgrades`.

## Backups and restore

Every night, `swvault-backup.timer` writes to your backup folder:

- `daily/gitea-*.tar.gz`: Gitea's database, config and repositories. 14 are kept.
- `daily/server-config-*.tar.gz`: `.env` (Gitea secrets), the Tailscale identity (so a restored server keeps its address), the admin token and `team.json`.
- `lfs/`: every file version. It's append-only, so each night copies only what's new.

Copy the backup folder to a second place now and then (another disk, cloud storage).

**Restore on a new PC:**

1. Clone the repo to `/opt/swvault`.
2. Unpack the newest `server-config-*.tar.gz` into `server/linux`.
3. Unpack the newest `gitea-*.tar.gz` into `server/linux/data/gitea`, following Gitea's [restore docs](https://docs.gitea.com/administration/backup-and-restore).
4. Copy `lfs/` to `server/linux/data/gitea/git/lfs/`.
5. Run `sudo ./setup.sh`. It finds the existing settings and identity, so the address and every member's sign-in stay the same.

If the old PC is still listed in the Tailscale admin console, remove it first so the name `swvault` is free.

## Good to know

- **Speed.** Funnel relays traffic through Tailscale's servers, and Tailscale applies bandwidth limits it doesn't publish. That's fine for day-to-day check-ins, but the first download of a large vault can take a while. The server also needs decent upload bandwidth, since it serves everyone's downloads.
- **Security.** The server is reachable from the internet, like any website. Registration is off, nothing is visible without signing in, and passwords are long and random. SwVault stores only per-PC access tokens, never passwords. Members can change their password on the server's web page.
- **The apartment network.** Tailscale works through NAT, carrier-grade NAT and most firewalls: it falls back to HTTPS on port 443 when UDP is blocked. If the building's network needs a browser sign-in (a captive portal), complete it once on the server PC, or register its MAC address with the building's network.
- **If Funnel doesn't suit you later.** Where you can forward ports 80/443, `server/docker` runs the same Gitea behind Caddy with a normal domain name. Change `vaultUrl` in `team.json`, rebuild the installer, and have members sign in again.

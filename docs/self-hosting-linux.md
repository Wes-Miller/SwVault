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
| (package with team.json)   |  *.ts.net   | (public relay)      |    |  + gitea, team service    |
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
- An email account for the server to send from, if new members must verify a school address (the default is @colorado.edu). A free Gmail account with an app password works; see step 2.

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
| Email domain to verify | `colorado.edu` | New members must prove they have an address there. `none` turns the check off. |
| SMTP server, port, address, password | `smtp.gmail.com`, `587`, `fsae.swvault@gmail.com`, app password | Only when verifying emails. The server uses this account to send verification codes and review emails. |
| Tailscale auth key | `tskey-auth-...` | From step 1. |

**Setting up a Gmail account to send from (5 minutes):** create a Gmail account for the team, turn on 2-Step Verification (<https://myaccount.google.com/security>), then create an app password (<https://myaccount.google.com/apppasswords>). Enter the Gmail address and the 16-letter app password when setup asks. Setup can then send you a test email; `sudo ./swvault-admin.sh test-email you@colorado.edu` sends another any time.

Then it:

- installs Docker, jq and curl;
- starts Tailscale, Gitea and the SwVault team service (invites, email checks, profiles, review emails);
- creates your admin account, the organization, the **Designers** (read/write) and **Viewers** (read-only) teams, and the vault repository with its review labels, with `main` protected against force-push;
- writes `team.json`;
- schedules nightly backups (03:15) and a 5-minute health watchdog;
- offers to stop the PC from sleeping, including ignoring a closed laptop lid;
- checks that the address answers from the internet. A brand-new name can take a few minutes to appear in public DNS.

Your admin password is in `data/admin-credentials.txt`, readable by root only. Running `setup.sh` again is safe: it keeps your data and fills in anything missing.

## 3. Build the team installer (Windows)

Copy `server/linux/team.json` to your Windows build PC and run:

```powershell
.\scripts\package.ps1 -Version 0.2.0 -TeamConfig .\team.json -Publish
```

This produces `dist\SwVault-FSAE-0.2.0.zip`, containing:

- SwVault (agent, CLI and SOLIDWORKS add-in);
- **MinGit**, a portable Git that the script downloads, so members don't install Git;
- `team.json`, so the installer knows which vault to join.

`-Publish` asks for your admin user name and password and uploads the zip to the server. Invite links then download it from `https://<server>/swvault-invites/download`. Without `-Publish`, share the zip yourself (for example on a shared drive).

### Pushing an update to the whole team

Build with a higher `-Version` and publish it:

```powershell
.\scripts\package.ps1 -Version 0.3.0 -TeamConfig .\team.json -Publish -Notes "Adds part numbering"
```

Every SwVault checks the server a minute after it starts and every 4 hours after that. When it finds a newer version, it shows a notification (with your notes) and a bold **Install update 0.3.0...** item on the tray icon. Clicking either one downloads the update, checks it against the SHA-256 the server recorded, waits until SOLIDWORKS is closed (it tells the member to close it), and installs it silently. The only prompt is Windows' administrator prompt. The new version then says "SwVault updated to 0.3.0". Members can also use tray icon → **Check for updates**.

- **`-Required`**: for changes everyone must have (for example a new vault format). The reminder then comes back at every check until they install it. Updates are never forced in the middle of someone's work.
- From the server instead: `sudo ./swvault-admin.sh publish-installer SwVault-FSAE-0.3.0.zip --notes "..." [--required]`.
- The newest published zip is also what invite links download, so new members always get the current version.
- Only installs in `C:\Program Files\SwVault` update themselves. Developer builds don't.

## 4. Sign in once yourself

Install the zip on your own PC (double-click **Install SwVault.cmd**) and sign in with your admin user name and password. Because the vault is new, your first sign-in sets it up: SwVault writes `.swvault/vault.json` with you as admin and approver.

## 5. Invite your team

In SOLIDWORKS, go to the **SwVault** tab → **Invite People** (or the SwVault tray icon → **Invite people...**):

1. Pick **Designer** (check out/in) or **Viewer** (read-only), how many people the invite is for, and how many days it lasts. One invite can cover the whole team: say, 25 people for 14 days.
2. Click **Create invite**. A ready-made message is copied to your clipboard:
   > You're invited to the FSAE vault… 1. Download the installer: https://swvault…/swvault-invites/download?invite=K7QM-R3XT-9BWE …
3. Paste it into your team chat or an email.

Revoke an invite any time from the same window. Only vault admins (the Owners team) can create invites. From the server, `sudo ./swvault-admin.sh invite --uses 25 --days 14` does the same.

For the new member, that's all:

1. Click the link, right-click the zip → **Extract All**, and double-click **Install SwVault.cmd**. The download is named after the invite, so the installer fills in the code.
2. In the SwVault window, choose **I'm new and have an invite**:
   - enter their school email, click **Email me a code**, and type in the 6-digit code;
   - enter their name, and pick **General member** or **Subteam lead of** (for example Chassis);
   - choose a user name and password.
3. The account is created, they're signed in, and the files download to `C:\SWVault\FSAE`. A tray notification says when they're all there.
4. They open SOLIDWORKS: the SwVault tab and task pane are ready.

The invite's limits (number of people, expiry, one account per email address) and the email check keep strangers out even if the link is shared further than you meant.

**Adding someone by hand instead** (no invite, no email check): `sudo ./swvault-admin.sh add-user alice --name "Alice Smith" --email alice@colorado.edu` prints a card with a generated password to send them privately. Add `--viewer`, `--approver` or `--admin` as needed. Give `--email` so they get review emails.

## Subteam leads and review requests

Everyone is either a **general member** or a **subteam lead** (with the subteam's name). People pick this when they join, and change it with the tray icon → **My team role...**. Existing accounts are asked once after signing in. **Becoming a lead needs an admin's approval** (stepping down doesn't): admins get a tray notification and an email, and approve in the tray icon → **Approvals...** or with `sudo ./swvault-admin.sh approvals` / `approve eli lead` / `decline eli lead`. An admin can also set it directly: `sudo ./swvault-admin.sh set-lead eli Suspension` or `set-lead eli --member`.

**Cars, subsystems and responsible engineers (REs).** Members add cars and their subsystems (each a vault folder) in **SwVault tab → Subsystems**, and ask to be an RE of a subsystem; a subsystem can have several. RE requests also need an admin's approval, the same way (`approve eli "Front Suspension"`, or the subsystem id if two share a name; `cars` lists cars, subsystems, their ids and REs). REs are notified of check-ins, new files and releases in their subsystem, and are copied on review requests general members make for it. The list is kept in the team service (`subsystems.json`) and backed up nightly with the other team data.

Members ask a lead for a review from SOLIDWORKS: open the file, then **SwVault tab → Request Review**. They choose **Design**, **Simulation** or **Drawing**, pick the lead (the list shows each lead's subteam) and add a note. The file must be checked in, so the lead reviews that exact version.

- **The lead** gets an email and a tray notification, and sees the request under **SwVault tab → Reviews → For me**. From there they open the file, then **Approve**, **Request changes** (with feedback) or **Comment**.
- **The subsystem's REs** (when a general member asked) are copied: an email and a notification, and the request under **Reviews → Copied to me (RE)**.
- **The member** gets an email and a notification when the lead approves or sends it back, with the lead's feedback. They see all their requests under **Reviews → My requests**, where they can reply or cancel.

Each request is also an issue in the vault repository on the server's web page (assigned to the lead, labeled with its kind and status), so the full discussion is kept and backed up. Emails go out once per event: the request opening, an approval, and each round of changes requested. They're only sent to people whose account has a real email address (everyone who joined with an invite does).

## Everyday admin

| Task | Command |
|---|---|
| Is everything up and reachable? | `sudo ./swvault-admin.sh status` |
| Invite people | SOLIDWORKS: SwVault tab → **Invite People**. Server: `invite [--viewer] [--uses N] [--days N]`, `invites`, `revoke-invite <code>` |
| Push an update to everyone | `scripts\package.ps1 -Version <higher> ... -Publish [-Notes "..."] [-Required]` (Windows) or `publish-installer <zip> [--notes "..."] [--required]` |
| Add / list people | `add-user <name> [--viewer\|--approver\|--admin] [--email a@colorado.edu]`, `list-users` (shows each person's team role) |
| Mark a subteam lead | `set-lead <name> <subteam>`, `set-lead <name> --member` |
| Lead and RE requests | `approvals`, `approve <name> lead\|<subsystem>`, `decline <name> lead\|<subsystem>`, `cars` |
| Check email works | `test-email <address>` |
| Forgotten password | `reset-password <name>` |
| Someone leaves | `disable-user <name>`. They can't sign in, and their history is kept. Undo with `enable-user`. |
| Make someone admin or approver | `set-role <name> admin\|approver [--remove]` |
| Back up now | `backup` |
| Update Tailscale | `update` (Gitea is pinned; bump `GITEA_TAG` in `.env` after reading its release notes) |
| Logs | `logs gitea`, `logs tailscale`, `logs invites` |

Run all of them with `sudo ./swvault-admin.sh` from `server/linux`. Also keep the Linux PC itself updated, for example with `sudo apt upgrade` monthly, or turn on `unattended-upgrades`.

## Backups and restore

Every night, `swvault-backup.timer` writes to your backup folder:

- `daily/gitea-*.tar.gz`: Gitea's database, config and repositories. 14 are kept.
- `daily/server-config-*.tar.gz`: `.env` (Gitea secrets and email settings), the Tailscale identity (so a restored server keeps its address), the admin token, `team.json`, and the team service's invites, profiles, cars/subsystems/REs and review-email log.
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

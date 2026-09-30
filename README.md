# SwVault

PDM for SOLIDWORKS teams (check-out/check-in, history, releases, reviews) on a free, self-hosted server. This page is how to set it up. Details: [self-hosting guide](docs/self-hosting-linux.md), [admin guide](docs/admin-guide.md), [user guide](docs/user-guide.md), [architecture](docs/architecture.md).

## What you need

- **A Linux PC for the server** that stays on and online: an old laptop, a mini PC or a Raspberry Pi 4/5 (64-bit), with 100–500 GB of disk. Ubuntu Server 24.04 LTS is easiest. No port forwarding or static IP needed.
- **A free Tailscale account** (<https://login.tailscale.com>). It gives the server a public HTTPS address; only the server runs Tailscale.
- **A Windows PC with the [.NET 10 SDK](https://dotnet.microsoft.com/download)** to build the team installer.
- **Optional: a Gmail account with an app password** so new members verify their school email and get review emails.
- Ideally a second drive or USB disk for nightly backups.

## 1. Get a Tailscale auth key

1. Sign in at <https://login.tailscale.com>.
2. **DNS**: turn on **MagicDNS** and click **Enable HTTPS**.
3. **Settings → Keys → Generate auth key** (not reusable, not ephemeral). Copy the `tskey-auth-...` key.

## 2. Set up the server (Linux PC)

```bash
sudo git clone https://github.com/wes-miller/swvault.git /opt/swvault
cd /opt/swvault/server/linux
sudo ./setup.sh
```

It asks for the team name (e.g. `FSAE`), your admin user name, the vault folder every Windows PC will use (e.g. `C:\SWVault\FSAE`), the SOLIDWORKS version, the email settings and the Tailscale key. It then installs Docker, starts Gitea and the SwVault team service at `https://<name>.<tailnet>.ts.net`, creates the vault, schedules backups, and writes `team.json`. Your admin password is in `data/admin-credentials.txt`. Running it again is safe.

## 3. Build and publish the team installer (Windows)

Copy `server/linux/team.json` to the Windows PC, then in this repository:

```powershell
.\scripts\package.ps1 -Version 1.0.0 -TeamConfig .\team.json -Publish
```

This builds `dist\SwVault-<Team>-1.0.0.zip` (SwVault, the SOLIDWORKS add-in and a portable Git) and uploads it to your server. Sign in with your admin user name and password when asked.

## 4. Install it yourself

Download `https://<your server>/swvault-invites/download`, right-click the zip → **Extract All**, and double-click **Install SwVault.cmd** (close SOLIDWORKS first; one Windows admin prompt). Sign in with your admin account; your first sign-in sets up the vault with you as admin. Open SOLIDWORKS: there's a **SwVault** tab and task pane.

To bring in existing files: SwVault tab → **Import Folder**.

## 5. Invite your team

SwVault tab → **Invite People** → choose **Designer** or **Viewer**, how many people and how many days → **Create invite**. Paste the copied message into your team chat. Members click the link, run **Install SwVault.cmd**, verify their school email, pick a user name and password, and their files download on their own.

Then, in SOLIDWORKS or the SwVault tray icon:
- **Subsystems** – add the car and its subsystems; members ask to be responsible engineers.
- **Approvals** (admins) – approve subteam lead and responsible-engineer requests.

## Updating everyone

Build with a higher version and publish it:

```powershell
.\scripts\package.ps1 -Version 1.1.0 -TeamConfig .\team.json -Publish -Notes "What's new"
```

Every member gets a notification and an **Install update** item on the tray icon; it installs after they close SOLIDWORKS. Add `-Required` to keep reminding people until they update.

## Day-to-day admin (on the server)

```bash
cd /opt/swvault/server/linux
sudo ./swvault-admin.sh status        # is everything up and reachable?
sudo ./swvault-admin.sh               # list all commands: users, roles, approvals, backups, logs
```

## Developers

Needs Windows, the .NET 10 SDK, Git for Windows and (for the add-in) SOLIDWORKS.

```powershell
dotnet build SwVault.slnx
dotnet test SwVault.slnx
.\scripts\dev-gitea.ps1 -Setup        # local Gitea for testing (gitea.exe in C:\SwVaultDev\gitea)
.\scripts\dev-register-addin.ps1      # load your build in SOLIDWORKS (close it first)
```

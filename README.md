# SwVault

SwVault is PDM for SOLIDWORKS teams that runs on a free Git LFS server, typically a self-hosted Gitea. It provides:

- **Check-out and check-in.** Checking out locks a file so nobody else can edit it. Checking in uploads the new version and releases the lock.
- **Assembly-aware operations.** Check-out, check-in and get latest follow assembly and drawing references, and you can see where a file is used.
- **History and rollback.** Every version is kept. You can get an older version, "as built" with its references, or roll back to it.
- **A release workflow.** Files move WIP → In Review → Released. Revision letters are stamped into the file on release, and PDF/STEP files can be exported automatically.
- **Review requests.** Members ask a subteam lead for a design, simulation or drawing review of a file. Leads approve it or send it back with feedback, and both sides get an email and a notification.
- **Easy onboarding.** Admins send an invite link. New members install, verify their school email, and choose a user name and password.
- **Bulk import.** An existing folder of files can be imported, and assembly references are re-pointed to the imported copies.

Everything happens inside SOLIDWORKS through the SwVault add-in, which has its own tab and a task pane.

## How it works

```
SLDWORKS.exe                     named pipe                per-user tray process               HTTPS
+-----------------------+  <--------------------->  +------------------------------+  <------>  Gitea (or GitHub)
| SwVault.AddIn (net48) |                           | SwVault.Agent (net10)        |            git repo + LFS content
| tab, task pane,       |   swvault CLI ----------> |  SwVault.Core: mirror, LFS,  |            + LFS file locks
| dialogs, SW events    |                           |  locks, state, jobs, sync    |
+-----------------------+                           +------------------------------+
```

- **Vault = one Git repository.** File content is stored in Git LFS. Check-out uses the LFS File Locking API. A check-in is one git commit containing the LFS pointer file and a metadata sidecar (`.swvault/meta/<path>.json`) for each file. The repository stays a standard Git LFS repository that you can browse in the server's web UI.
- **Your vault folder (for example `C:\SWVault\FSAE`) is not a git working copy.** The agent manages it file by file, the way PDM manages its local cache:
  - get latest works on single files or whole assemblies;
  - files you haven't checked out are read-only;
  - files you didn't ask for are never touched.
- **The agent** runs per user in the system tray. It does all network work, polls for new versions and check-outs, shows notifications, and serves the add-in over a named pipe. The add-in only handles SOLIDWORKS-specific work: saving, releasing and reloading open documents, reading references and custom properties, and stamping revisions.

See [docs/architecture.md](docs/architecture.md), [docs/admin-guide.md](docs/admin-guide.md) and [docs/user-guide.md](docs/user-guide.md).

**Hosting it yourself:** [docs/self-hosting-linux.md](docs/self-hosting-linux.md) sets up the server on any Linux PC, even on an apartment network with no port forwarding, with one script (`server/linux/setup.sh`). It also produces the config for a team installer, so members only install SwVault and sign in.

## Repository layout

| Path | What |
|---|---|
| `src/SwVault.Protocol` | Pipe protocol and DTOs (netstandard2.0, no dependencies, shared with the add-in) |
| `src/SwVault.Core` | Vault engine: pointer-only git mirror, LFS and lock clients, SQLite state, all operations |
| `src/SwVault.Agent` | Tray agent: pipe server, job runner, sync loop, notifications |
| `src/SwVault.Cli` | `swvault` command line for scripting, admin and testing |
| `src/SwVault.AddIn` | SOLIDWORKS add-in (net48 x64) |
| `tests/` | Unit tests, offline integration tests (fake LFS server), opt-in tests against a real Gitea |
| `tools/SwApiCheck` | Verifies the SOLIDWORKS API behaviors SwVault relies on (run after SOLIDWORKS upgrades) |
| `scripts/` | Developer setup: local Gitea, add-in registration |
| `server/` | Deploying the vault server (Gitea). `server/linux` is the one-script setup for a self-hosted Linux PC (Tailscale Funnel + Gitea in Docker) |

## Developer quick start

You need:
- Windows
- the .NET 10 SDK
- Git for Windows 2.31 or newer
- SOLIDWORKS, only needed for the add-in

```powershell
dotnet build SwVault.slnx
dotnet test SwVault.slnx                  # offline: fake LFS server + local bare repos
```

**Run against a real Gitea on this PC:**

```powershell
.\scripts\dev-gitea.ps1 -Setup            # gitea.exe must be in C:\SwVaultDev\gitea
$env:SWVAULT_GITEA_TESTS = "1"; dotnet test tests\SwVault.IntegrationTests --filter GiteaSmokeTests
```

**Try the CLI as two users on one PC:**

```powershell
$sv = "$env:LOCALAPPDATA\SwVaultBuild\bin\SwVault.Cli\debug\swvault.exe"
& $sv --profile alice vault init http://127.0.0.1:3000/fsae/vault.git --name FSAE --root C:\SWVaultDev\alice\FSAE --user alice --token <token>
& $sv --profile bob vault add http://127.0.0.1:3000/fsae/vault.git --root C:\SWVaultDev\bob\FSAE --user bob --token <token>
& $sv --profile alice checkin C:\SWVaultDev\alice\FSAE\Part.SLDPRT -m "first version"
& $sv --profile bob get
```

**Install the add-in for development.** Close SOLIDWORKS first. The script shows one UAC prompt, on the first run only:

```powershell
.\scripts\dev-register-addin.ps1
```

**After a SOLIDWORKS upgrade**, confirm the API still behaves the way SwVault expects. SwApiCheck starts its own SOLIDWORKS, so close yours first:

```powershell
& "$env:LOCALAPPDATA\SwVaultBuild\bin\SwApiCheck\debug\SwApiCheck.exe"
```

Build output goes to `%LOCALAPPDATA%\SwVaultBuild` whenever the source is inside OneDrive, because OneDrive sync fights with build output.

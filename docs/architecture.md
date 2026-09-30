# SwVault architecture

## Components

| Component | Runs in | Responsibility |
|---|---|---|
| `SwVault.AddIn` (net48 x64) | SLDWORKS.exe | Commands, task pane, dialogs. Saves open documents, releases/reloads documents whose files get replaced, reads references and custom properties, stamps revisions, exports PDF/STEP. No third-party dependencies (they would load into SOLIDWORKS). |
| `SwVault.Agent` (net10, tray) | per user | Named-pipe server (`SwVault.<user SID>`, ACL = current user). Runs jobs, polls the server (ls-remote every ~30 s, locks every ~60 s), shows notifications. |
| `SwVault.Core` | agent, CLI | The vault engine (below). |
| `SwVault.Protocol` (netstandard2.0) | both sides | Newline-delimited JSON RPC (`RpcMessage` envelope, DTOs, `AgentClient`). |
| `swvault` CLI | terminal | Scripting, admin (init, unlock, import), diagnostics (`doctor`). Uses Core directly. |

## Storage model

A vault is one Git repository on a Git LFS server with file locking (Gitea, GitHub, GitLab).

```
.gitattributes                 * filter=lfs diff=lfs merge=lfs -text lockable  (SwVault files excepted)
.swvault/vault.json            shared settings (roles, workflow, local root, SOLIDWORKS version)
.swvault/meta/<path>.json      sidecar per file: version, oid, size, who/when/comment, state,
                               revision (+history), references [{path, version, oid}], properties
<folders>/<file>               Git LFS pointer (content lives in the LFS store)
```

| Concept | Implementation |
|---|---|
| Version number | `meta.version`. Incremented only when content changes; workflow transitions update the sidecar without a new version. |
| "As built" | Each reference records the exact child version and oid the author had when the parent was checked in. |
| Check-out | `POST {repo}.git/info/lfs/locks`. Exclusive per path, visible to everyone, and the same locks plain `git lfs` users see. |
| Check-in | One commit with pointers and sidecars for every file in the check-in. Built from a temp index in a bare, pointer-only mirror (`read-tree`, `hash-object`, `update-index`, `write-tree`, `commit-tree`) and pushed. A non-fast-forward rejection means someone else pushed first: SwVault re-reads head, re-validates that its base versions are unchanged, rebuilds and retries. |
| History | `git log` of the sidecar. |

## Workspace model

The vault folder is **not** a git working tree. Per file, the agent's SQLite state stores:
- the base version (the oid and version the local content came from);
- a size/mtime snapshot taken right after SwVault wrote the file;
- a cached content hash;
- whether the lock was taken from this PC.

The status of a file combines three things:

| Part | Values |
|---|---|
| Local state | NotLocal / UpToDate / Outdated / Modified / Conflict / LocalOnly / MissingLocally / DeletedOnServer |
| Lock | none / mine here / mine on another PC / someone else |
| Workflow | state + revision |

Files not checked out by this PC are read-only (file attribute).

## Replacing files that are open

Operations that write to the workspace are staged: content is downloaded and verified into `<root>\.swvault-local\tmp` first.

1. If any target files exist, the agent answers `ReadyToApply` with the list of files it will replace.
2. The add-in releases the open documents:
   - parts and assemblies: `ForceReleaseLocks`;
   - drawings: closed;
   - documents with unsaved changes: skipped (their files stay untouched).
3. The add-in calls `job.apply`.
4. The add-in reloads (`ReloadOrReplace`) or reopens, then syncs SOLIDWORKS' read-only flag (`SetReadOnlyState`).

`tools/SwApiCheck` verifies these behaviors. It found that SOLIDWORKS locks every loaded file, read-only or not, which is why the release step is required.

## Auth

The credential chain, tried in order:
1. `SWVAULT_USER`/`SWVAULT_TOKEN`;
2. SwVault's own entry in Windows Credential Manager (`SwVault:<server>`);
3. Git Credential Manager (`git credential fill`), which does browser sign-in.

The credential is sent as an HTTP Basic header:
- to LFS and API endpoints directly;
- to git through `GIT_CONFIG_*` environment variables (`http.<server>/.extraHeader`), so it never appears on a command line.

## Failure handling

- **Crash between push and unlock:** a journal entry lets the agent finish the unlocks when it restarts.
- **Offline:** read-only views work from the last sync. Operations that need the server fail with a clear message.
- **Lost lock** (an admin force-released it): check-in refuses with a conflict instead of overwriting the other person's work.
- **Newer SOLIDWORKS release:** the version guard rejects the check-in.

# SwVault user guide

SwVault keeps the team's SOLIDWORKS files on the vault server and makes sure only one person edits a file at a time.

## Getting set up

1. Close SOLIDWORKS.
2. Click the download link in your invite, right-click the zip → **Extract All**, and double-click **Install SwVault.cmd**. Approve the prompt.
3. In the SwVault window:
   - **New here?** Choose **I'm new and have an invite**. The code is usually filled in already. Enter your school email, click **Email me a code**, and type in the 6-digit code from the email. Then enter your name, say whether you're a **general member** or a **subteam lead** (and which subteam), and choose a user name and password.
   - **Already have an account?** Choose **I have an account** and sign in.
4. Your team's files download to the vault folder, for example `C:\SWVault\FSAE`. The SwVault icon in the system tray tells you when they're all there.
5. Open SOLIDWORKS. The **SwVault** tab and task pane are ready.

Closed the sign-in window? Double-click the SwVault tray icon, or click **Connect vault...** in the SwVault task pane.

## The basics

- **Your vault folder**, for example `C:\SWVault\FSAE`, is the same path on every team PC. Always open and save vault files from there. Don't put it in OneDrive, Dropbox or Google Drive.
- **Files you haven't checked out are read-only.** SOLIDWORKS opens them read-only and won't let you save over them.
- **Check Out** before you edit. This locks the file for you. Everyone else sees "checked out by you" and can't check it out until you're done.
- **Check In** when you're done. This uploads your changes as a new version, releases the lock, and makes the file read-only again. Always write a short comment about what changed.
- **Get Latest** downloads newer versions that teammates checked in. The tray icon tells you when files you have were updated.

## In SOLIDWORKS

The **SwVault** tab (CommandManager) and the **SwVault task pane** (right side) have everything you need. Commands act on the active document, or on the components you selected in an assembly's FeatureManager tree (right-click → SwVault).

| Command | What it does |
|---|---|
| Add to Vault | Puts the active document in the vault in one step. If it has never been saved, or is saved outside your vault folder, you pick where in the vault folder it goes. It is then saved and checked in, together with any new files it references, with the comment "Added <file>". |
| Check Out | Locks the file(s) and gets the latest version first. For an assembly or drawing, you tick which referenced files you also want to edit. |
| Check In | Lists what you changed or added under the file (including new parts in an assembly), asks for a comment, uploads, releases. Tick **Keep checked out** to keep working. |
| Undo Check Out | Throws away your changes and releases the lock. |
| Get Latest | Downloads the newest versions of the file and everything it references. Open documents reload automatically. |
| History | All versions with who, when and comment. **Get This Version** puts an older version in your folder, read-only. Tick "referenced files as they were" for an assembly exactly as it was built. **Roll Back** (after checking out) makes an old version the newest again when you check in. |
| Where Used | Assemblies and drawings that use this file. |
| Change State | Workflow: Submit for review → Approve (release) / Reject; Change request to edit a released file again. |
| Request Review | Asks a subteam lead for a **design**, **simulation** or **drawing** review of the file. You pick the lead and add a note. The lead reviews the version that's checked in. |
| Reviews | Your review inbox: **For me** (requests sent to you as a lead: open the file, **Approve**, **Request changes**, **Comment**) and **My requests** (what you asked for and what the lead said). |
| Invite People | Vault admins: makes an invite link and code to paste into the team chat. |
| Import Folder | Copies an existing folder of SOLIDWORKS files into the vault and fixes the references. Usually done once, by an admin. |
| Refresh / Vaults | Re-check the server, or connect to a vault. |

**Task pane:**
- Browse vault folders and see each file's state: up to date, outdated, modified, new, not local, or checked out by someone.
- Filter to *My check-outs*, *Outdated*, *In review* or *New and modified*.
- Search file names and properties (part numbers, descriptions).
- Double-click a file to open it. SwVault first offers to get the latest versions of everything it references.

**New files:** click **Add to Vault** (on the SwVault tab, or the button at the bottom of the task pane). That's all: SwVault saves the file into your vault folder if it isn't there yet and checks it in, including new parts an assembly uses. Afterwards the file is read-only like every other vault file; check it out to keep editing. You can still save new files inside the vault folder yourself; they show as *New* until you add or check them in.

## Reviews

- **Asking for a review:** check the file in, then **Request Review**. You'll get an email and a tray notification when the lead approves it or asks for changes. If they ask for changes, their feedback is in the email and under **Reviews → My requests**. Make the changes, check in, and request another review.
- **If you're a lead:** new requests arrive by email and as a tray notification, and wait under **Reviews → For me**. **Request changes** needs a comment saying what to change. **Approve** can have one too.
- **Your team role** (general member or subteam lead, and which subteam): tray icon → **My team role...**. Only leads appear in the Request Review list.

Reviews are separate from release approval (**Change State → Approve**), which stamps revisions. A review is feedback from your lead. Many teams get a review before submitting a file for release.

## Workflow and revisions

| State | Meaning |
|---|---|
| WIP | Being designed. Check out/in freely. |
| InReview | Submitted for review. Read-only until an approver approves or rejects it. |
| Released | Approved. The revision letter (A, B, C…) is written into the file's `Revision` property, and a PDF of the drawing is saved in `_Released`. Read-only; use **Change request** to edit again. The next release gets the next letter. |
| Obsolete | No longer used. |

Only approvers can approve. The admin sets who they are.

## When something looks wrong

| You see | Do this |
|---|---|
| "checked out by Alex" | Ask Alex to check it in. If Alex is gone, an admin can release it. |
| Outdated | Get Latest. |
| Conflict | Someone checked in a newer version while you edited an old copy. Save your work as a copy, Undo Check Out, Get Latest, and redo the change on the newest version. |
| "checked out by you on another computer" | Check it in there, or choose to take over the check-out here. |
| Can't save a file | It's read-only because you haven't checked it out. Check it out. |
| Everything offline | Are you connected to the internet (or on campus / the CU VPN, if your team's server is on campus)? Is the tray icon running? |

Run `swvault doctor` in a terminal for a quick health check of your PC's setup.

# Singularity Sync

A native Windows app with two separate modes: direct access to server files using Windows SMB, and two-way file synchronization on a trusted LAN. Built with C# / .NET 10 and Windows Forms. No third-party application libraries.

## Direct server access (SMB) — CrossLink network installations

Use **Direct access (SMB)** for an application that must open the same files on the server. Do **not** synchronize an active CrossLink database. CrossLink compatibility depends on its supported product/year network installation; this app configures file access, not CrossLink's installer, database engine, licensing, or backups.

### On the server PC

1. Open the app normally, select **Direct access (SMB)**, and enable **Use direct access on this PC**. This stops sync and suppresses automatic sync starts, including the old sign-in entry.
2. Close CrossLink on all PCs. Click **Set up this PC as server… (administrator)** and accept Windows' UAC prompt. The setup window runs separately; the main app stays under your normal Windows account.
3. Browse to CrossLink's supported local server folder. Use an unused share name (default `CrossLink`) and local account name (default `SingularityTax`). Record the generated password using **Show password**; the server setup does not save it in app settings or print it in logs.
4. Click **Create server access**. Record the displayed network path, such as `\\OFFICE-PC\CrossLink`, and account, such as `OFFICE-PC\SingularityTax`.

Setup creates a dedicated non-administrator local account, grants inheritable NTFS Modify and SMB Change permissions, requires share encryption, disables Offline Files caching for that share, and opens TCP 445 only for the local subnet on Private networks. The generated account/password do not expire automatically; protect the password and remove/recreate managed access when rotating it. Setup starts the Windows Server service but does not weaken signing, enable guest access, enable SMB1, change network classification, or overwrite existing users/shares. A disabled SMB2/3 configuration, conflicting names, unsupported paths, or policy restrictions produce an in-app error. Domain policy can still override local configuration.

The folder must not overlap a synchronized folder, contain sync metadata, or contain links/junctions. Existing sync folders require a deliberate migration while CrossLink and sync are stopped, following CrossLink-supported procedures; setup does not delete or migrate sync state. A `.singularity-smb` marker reserves the created share folder for direct access. Do not delete it while the share is in use. The sync engine refuses marked folders, their descendants, and scans containing them. Protected child ACLs are not replaced; the workstation test checks the share root, and CrossLink's actual subfolders must also be verified.

Server access remains available after the app exits and depends on Windows SMB, the server being awake, and LAN connectivity. App Stop controls affect file sync, not SMB shares. To remove this app's server access, open server setup, enter its original folder and share/account names, and choose **Remove managed server access**. This removes the managed share, account, its ACL grants and firewall rule; files remain. Removal refuses observed open SMB files. Unrelated shares/accounts and firewall rules are left alone. Failed creation attempts roll back created resources where possible and report cleanup failures.

### On each workstation

1. Open the app **without Run as administrator**, enable **Use direct access on this PC**, and enter the server computer name or IPv4 address, share name, local account from setup, and generated password. Use `SERVER\SingularityTax`, not a Microsoft account or PIN.
2. If CrossLink requires a mapped drive, enter its required unused letter (for example `X:`). Otherwise leave it blank and use the UNC path. Configure CrossLink according to its supported network installation.
3. Click **Connect & save**, then **Test access**. The test creates a uniquely named temporary file, checks read/write and exclusive open from this workstation, then removes the test file. It does not open or alter database files and does not certify multi-PC database locking.
4. Use **Open folder** to inspect the server files. Verify CrossLink can open its existing data and operate correctly from two physical PCs before using this with live tax work.

Passwords are stored as app-specific Generic credentials in **Windows Credential Manager** for the current user; the JSON preference file contains only non-secret connection details. The app reconnects the remembered share when reopened. A mapped drive is registered with Windows for persistence, but Windows logon reconnection may still require reopening this app to supply its saved credentials. Closing the app leaves connections available to CrossLink; losing the network does not provide an offline database copy.

**Incorrect credentials / error 1219:** use **List connections** to find sessions already using the server with another account. Close CrossLink and affected files, enter the conflicting server/share and optional drive into the form, and use **Disconnect selected**. For a listed `IPC$` connection, enter `IPC$` as the share and leave drive blank. The app never force-closes files or disconnects all server sessions. Then re-enter the managed share details and reconnect. **Forget saved password** removes only the app's saved credential, not existing Windows sessions or other programs' credentials.

This mode uses Windows SMB underneath, so it can bypass Microsoft-account credential issues but cannot fix every Windows update, SMB policy, or network failure. App-created shares require encryption; connections to pre-existing shares follow those shares' settings. Use CrossLink-supported backups: sync history does not capture SMB database activity.

## Run file sync (ordinary files only)

Use `artifacts/win-x64/SingularitySync.exe`, or extract `artifacts/SingularitySync-win-x64.zip` on each computer. The portable build includes .NET; there is no installer or runtime prerequisite. Target: Windows 10/11 x64. An ARM64 build is available through the build script, but has not been tested on ARM hardware.

1. **Server:** choose **Server**, select the folder you own, and click **Start server**. The app shows a six-digit pairing code, available IP addresses, and port 45831.
2. **Client:** choose **Client** and select an existing local folder. LAN discovery runs automatically; **Search LAN** refreshes it. Select the server, enter its code, then click **Pair & start**. For manual pairing, enter the server IP, port, and code. Reopening a paired client automatically starts sync with its remembered server and folder, retrying if the server is offline.
3. Repeat on additional client computers. Existing files merge on the first sync. If both sides have different contents under the same filename, the server version keeps the original name and the client version is saved and synchronized as a ` (conflict ...)` copy.
4. On subsequent launches, click **Start sync**. Pairing, mode, folder, and the last server address are remembered. The window close button hides the app in the system tray and synchronization continues. Double-click its tray icon or choose **Open Singularity Sync** to restore the window. Choose **Exit** from the tray menu to stop synchronization and quit; **Stop** pauses synchronization without quitting. In Server mode, enable **Start server when I sign in to Windows** after choosing your folder to launch the app and start sharing at Windows sign-in. This is off by default; uncheck it to remove the sign-in registration. While enabled, opening the app also starts the server automatically. The saved mode must be Server; Client mode never auto-starts sharing. If you move the portable executable, turn the toggle off and back on from its new location. This runs under your Windows account after sign-in, not as a Windows service.

Hover over the tray icon for this app's upload/download speeds and connected PC count. Single-click it (or choose **Network activity** from its menu) for live speeds, sent/received totals since app launch, and PC names with **Syncing**, **Idle**, or **Offline** status. Double-click still opens the main window. Speeds are approximate two-second averages of streamed file data; totals include retries and partial transfers, exclude protocol/discovery traffic, and survive Stop/Start until the app exits. KB/MB/GB use decimal units. A silent peer becomes offline after 45 seconds. Server mode lists clients seen this run; client mode lists its server. Update both PCs to see client names; older clients appear as **Unknown PC** with their address. Existing pairings remain valid.

The **Setup** tab contains mode, folder, and pairing controls. The **Activity** tab contains the sync log.

File edits and deletions flow in both directions. Each client can modify the entire shared folder. Server mode is the central authority for revisions and conflict resolution; it does not make the folder read-only for clients.

## LAN access and pairing

- TCP **45831** carries pairing, manifests, change notifications, and file transfers. UDP **45832** handles LAN discovery.
- Allow the app through Windows Firewall on your **Private** network. If prompted, approve Private-network access. The app does not alter firewall rules itself.
- If needed, run the included `Enable-LanFirewall.ps1` from an elevated PowerShell window on both the server and client. It creates rules limited to this executable, the Private profile, and the local subnet. From source: `./scripts/Enable-LanFirewall.ps1 -ProgramPath ./artifacts/win-x64/SingularitySync.exe`. The client rule allows discovery replies from UDP 45832 to the temporary port used by each search. Moving the executable requires updating the rules.
- Guest Wi-Fi, client isolation, blocked broadcast packets, and different subnets can prevent discovery. Manual IP pairing works when TCP access is available. Automatic address recovery needs UDP discovery to reach the server.
- A code lasts ten minutes; **New pairing code** replaces it. Several clients can use a current code. A successful handshake issues a random 256-bit bearer token; later requests use that saved token without asking for the code again. Pairing attempts are rate limited to five per minute per IP. **Forget all clients** revokes all saved authorizations immediately.
- The server advertises a persistent device ID, folder ID, and active adapter MAC addresses. When its saved IP stops responding or belongs to another identity, a client searches again, checks the same device/folder and an overlapping MAC when both sides advertise MACs, and saves the new address. A DHCP change needs no new pairing. Replacing all network adapters may require pairing again.
- MAC addresses and discovery IDs are identification hints, not cryptographic proof. This is deliberately **unencrypted HTTP**, including pairing codes, tokens, filenames, and file contents. Use it only on a trusted LAN; do not forward its ports to the Internet.

## Sync behavior and recovery

- Filesystem watchers wake synchronization after a short 150 ms settling delay. Server change notifications use HTTP long polling; clients also check every two seconds. Connection failures trigger automatic retries and LAN rediscovery.
- SHA-256 hashes identify file contents. Unchanged files use a size/mtime cache; watcher events invalidate only the affected paths (including directory descendants), and watcher errors clear the cache. A full hash scan still runs at least once every 60 seconds when sync is checking. There is no clock-based conflict resolution.
- Whole files stream to staging storage, are checksum verified, and replace the destination atomically. Uploads require the server revision the client last observed; stale writes are rejected and reconciled on retry. A partial transfer never replaces a destination. Interrupted transfers restart from the beginning.
- Bulk transfers process files sequentially with bounded streaming buffers. Each transfer verifies and publishes only its target instead of rescanning and rehashing the entire server folder. Background scans coalesce watcher bursts to at most one scan every two seconds and still discover outside edits; unchanged history snapshots reuse an in-memory cache. Small atomic per-file journal records preserve completed revisions and client baselines, with full state checkpoints every 128 changes, at the end of a client sync pass, and on server shutdown. Journals replay automatically after restart. Disk speed, per-file history copies, total bytes, and LAN latency still affect batch time.
- Offline edits reconcile against a persisted baseline. Concurrent edits preserve the client's content under a conflict filename and keep the server's version at the original path. A server deletion versus a client edit preserves the client edit as a conflict file; a server edit versus a client deletion restores the server version.
- Renames are represented as deletion plus creation. Empty directories, case-only renames, ACLs, original timestamps, alternate data streams, and symlinks/junctions are not synchronized. File/directory collisions created independently on different peers may require manual resolution. Reparse points pause syncing rather than being followed outside the folder.
- Locked or continuously changing files can defer the current sync pass until available. Use ordinary local folders, not live database directories or cloud-placeholder folders. The app retries failures automatically and records them in Activity.
- A `.singularity-sync` directory in each selected folder holds local state and pending state journals, transfer staging, a folder ownership lock, and recovery copies. It is excluded from synchronization. Do not edit, remove, or copy this metadata between computers while using the app. A client folder previously paired with a different server/folder is rejected; choose a new folder when changing that pairing.
- **Server file history:** Server mode automatically keeps the latest **10 captured versions per file**, including the latest captured content, under `<selected-folder>/.singularity-sync/history/`. Existing files are captured when the server starts. Client uploads and observed direct server edits add a snapshot only when content changes; the oldest snapshot is removed after the new one is verified and saved. Deleting a file retains its history. Renames keep history at the old path and start history at the new path. History survives restarts and is never synchronized to clients. A failed snapshot pauses that sync pass and is retried, with the error recorded in Activity. Briefly interrupted pruning may leave extra copies until the next successful scan of that file.
- **Restore a server version:** click **Open file history** in Server mode. Each file has a stable ID folder with an `index.json` recording its original relative path, snapshot IDs, UTC capture times, and SHA-256 hashes. Inside each timestamp/ID folder is a normal copy with its original filename. Stop sync, copy the desired snapshot to its original path in the selected folder, then start the server again. The restored content synchronizes normally. Copy snapshots out; do not modify history files or indexes in place.
- **History limits:** snapshots cover content observed by the running server, not every intermediate save during rapid edits or while the app is stopped. On restart, the last saved snapshot remains and the current content is captured. History uses full file copies, so ten large versions can use substantial disk space. Deleted-file history is retained indefinitely, up to ten snapshots per original path. Symlinks, junctions, and other unsupported sync entries are not captured.
- **Client recovery and existing backups:** clients continue retaining overwritten/deleted files under `.singularity-sync/recovery/<timestamp-id>/<original-relative-path>`. Stop sync and copy a desired version back to restore it. Existing recovery folders from earlier server versions are left intact; new server changes use bounded file history instead. Client recovery copies, legacy server recovery copies, and deletion records are not automatically pruned.
- Preferences and authorization tokens live in `%LOCALAPPDATA%\SingularitySync\settings.json`. Tokens are stored in plaintext under the Windows user profile. Only one app instance can run at a time, including while hidden in the tray. Another launch displays an already-running message; restore the existing app from its tray icon. The executable, window, taskbar, and tray share the same overlapping-squares icon. The sync root should be a dedicated subfolder, not an entire drive.

## Build and verify

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) on Windows. A local SDK at `.tools/dotnet/dotnet.exe` is also supported by the build script.

```powershell
./build.ps1
# Optional ARM64 package:
./build.ps1 -Runtime win-arm64
```

The script runs the integration suite, creates a self-contained single executable, and packages it with this guide and the optional firewall script. Build outputs and the local SDK are ignored by Git. The executable is unsigned.

```powershell
dotnet build SingularitySync.App -c Release
dotnet run --project SingularitySync.Tests -c Release
# Optional 3,000-file upload, fan-out, and restart verification with loopback timings:
dotnet run --project SingularitySync.Tests -c Release -- --bulk
```

The dependency-free integration runner uses temporary folders and a real HTTP server, two clients, and UDP discovery. It checks pairing, authorization, two-way updates, fan-out, nested/binary/empty files, conflict recovery, deletions, renames, optimistic concurrency, checksums, unsafe paths, folder ownership, endpoint rediscovery, persisted restart state, journal recovery, targeted cache invalidation and transfer verification, revocation, and automatic watcher updates. Tests delete only their uniquely created temporary directory. Same-machine integration checks do not replace a two-physical-PC LAN test, especially for firewall and DHCP behavior.

Source layout:

- `SingularitySync.App`: native Windows interface and entry point.
- `SingularitySync.Core`: discovery, protocol models, folder storage, server, and client reconciliation.
- `SingularitySync.Tests`: executable integration suite.
- `build.ps1`: test, publish, and package.
- `scripts/Enable-LanFirewall.ps1`: optional elevated Private-LAN firewall setup.

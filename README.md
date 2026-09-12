# Singularity Sync

A small native Windows app for fast, two-way file synchronization on a trusted LAN. One computer serves one folder; paired clients synchronize their own local folder with that server. Built with C# / .NET 10, Windows Forms, and a streaming HTTP service. No third-party application libraries.

## Run

Use `artifacts/win-x64/SingularitySync.exe`, or extract `artifacts/SingularitySync-win-x64.zip` on each computer. The portable build includes .NET; there is no installer or runtime prerequisite. Target: Windows 10/11 x64. An ARM64 build is available through the build script, but has not been tested on ARM hardware.

1. **Server:** choose **Server**, select the folder you own, and click **Start server**. The app shows a six-digit pairing code, available IP addresses, and port 45831.
2. **Client:** choose **Client** and select an existing local folder. LAN discovery runs automatically; **Search LAN** refreshes it. Select the server, enter its code, then click **Pair & start**. For manual pairing, enter the server IP, port, and code.
3. Repeat on additional client computers. Existing files merge on the first sync. If both sides have different contents under the same filename, the server version keeps the original name and the client version is saved and synchronized as a ` (conflict ...)` copy.
4. On subsequent launches, click **Start sync**. Pairing, mode, folder, and the last server address are remembered. Keep both apps running; **Stop** pauses synchronization. The app does not start automatically with Windows or run as a Windows service.

File edits and deletions flow in both directions. Each client can modify the entire shared folder. Server mode is the central authority for revisions and conflict resolution; it does not make the folder read-only for clients.

## LAN access and pairing

- TCP **45831** carries pairing, manifests, change notifications, and file transfers. UDP **45832** handles LAN discovery.
- Allow the app through Windows Firewall on your **Private** network. If prompted, approve Private-network access. The app does not alter firewall rules itself.
- If needed, run the included `Enable-LanFirewall.ps1` from an elevated PowerShell window on the server. It creates rules limited to this executable, the Private profile, and the local subnet. From source: `./scripts/Enable-LanFirewall.ps1 -ProgramPath ./artifacts/win-x64/SingularitySync.exe`. Moving the executable requires updating the rules.
- Guest Wi-Fi, client isolation, blocked broadcast packets, and different subnets can prevent discovery. Manual IP pairing works when TCP access is available. Automatic address recovery needs UDP discovery to reach the server.
- A code lasts ten minutes; **New pairing code** replaces it. Several clients can use a current code. A successful handshake issues a random 256-bit bearer token; later requests use that saved token without asking for the code again. Pairing attempts are rate limited to five per minute per IP. **Forget all clients** revokes all saved authorizations immediately.
- The server advertises a persistent device ID, folder ID, and active adapter MAC addresses. When its saved IP stops responding or belongs to another identity, a client searches again, checks the same device/folder and an overlapping MAC when both sides advertise MACs, and saves the new address. A DHCP change needs no new pairing. Replacing all network adapters may require pairing again.
- MAC addresses and discovery IDs are identification hints, not cryptographic proof. This is deliberately **unencrypted HTTP**, including pairing codes, tokens, filenames, and file contents. Use it only on a trusted LAN; do not forward its ports to the Internet.

## Sync behavior and recovery

- Filesystem watchers wake synchronization after a short 150 ms settling delay. Server change notifications use HTTP long polling; clients also check every two seconds. Connection failures trigger automatic retries and LAN rediscovery.
- SHA-256 hashes identify file contents. Unchanged files use a size/mtime cache; watcher events invalidate it, and a full hash scan runs at least once every 60 seconds when sync is checking. There is no clock-based conflict resolution.
- Whole files stream to staging storage, are checksum verified, and replace the destination atomically. Uploads require the server revision the client last observed; stale writes are rejected and reconciled on retry. A partial transfer never replaces a destination. Interrupted transfers restart from the beginning.
- Offline edits reconcile against a persisted baseline. Concurrent edits preserve the client's content under a conflict filename and keep the server's version at the original path. A server deletion versus a client edit preserves the client edit as a conflict file; a server edit versus a client deletion restores the server version.
- Renames are represented as deletion plus creation. Empty directories, case-only renames, ACLs, original timestamps, alternate data streams, and symlinks/junctions are not synchronized. File/directory collisions created independently on different peers may require manual resolution. Reparse points pause syncing rather than being followed outside the folder.
- Locked or continuously changing files can defer the current sync pass until available. Use ordinary local folders, not live database directories or cloud-placeholder folders. The app retries failures automatically and records them in Activity.
- A `.singularity-sync` directory in each selected folder holds local state, transfer staging, a folder ownership lock, and recovery copies. It is excluded from synchronization. Do not edit, remove, or copy this metadata between computers while using the app. A client folder previously paired with a different server/folder is rejected; choose a new folder when changing that pairing.
- **Recovery:** overwritten/deleted files are retained under `.singularity-sync/recovery/<timestamp-id>/<original-relative-path>` on the computer where the app replaced or removed them. Stop sync and copy a desired version back to restore it. Direct edits on the server cannot be backed up retrospectively by the app. Recovery copies and deletion records are not automatically pruned; review disk usage periodically.
- Preferences and authorization tokens live in `%LOCALAPPDATA%\SingularitySync\settings.json`. Tokens are stored in plaintext under the Windows user profile. Run one app instance per Windows user. The sync root should be a dedicated subfolder, not an entire drive.

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
```

The dependency-free integration runner uses temporary folders and a real HTTP server, two clients, and UDP discovery. It checks pairing, authorization, two-way updates, fan-out, nested/binary/empty files, conflict recovery, deletions, renames, optimistic concurrency, checksums, unsafe paths, folder ownership, endpoint rediscovery, persisted restart state, revocation, and automatic watcher updates. Tests delete only their uniquely created temporary directory. Same-machine integration checks do not replace a two-physical-PC LAN test, especially for firewall and DHCP behavior.

Source layout:

- `SingularitySync.App`: native Windows interface and entry point.
- `SingularitySync.Core`: discovery, protocol models, folder storage, server, and client reconciliation.
- `SingularitySync.Tests`: executable integration suite.
- `build.ps1`: test, publish, and package.
- `scripts/Enable-LanFirewall.ps1`: optional elevated Private-LAN firewall setup.

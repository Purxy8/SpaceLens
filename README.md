# SpaceLens

SpaceLens is a friendly Windows disk-space analyzer. It scans a drive or folder, explains where the used space is going, and lets you review individual files before sending them to the Recycle Bin.

[Download the latest installer](https://github.com/Purxy8/SpaceLens/releases/latest/download/SpaceLens-Setup.exe) · [View all releases](https://github.com/Purxy8/SpaceLens/releases) · [Privacy](PRIVACY.md) · [Code signing policy](CODE_SIGNING_POLICY.md) · [Security](SECURITY.md)

## Highlights

- Familiar decimal storage units (`B`, `KB`, `MB`, `GB`, and `TB`) throughout the interface
- A brighter, more polished layout with clearer status feedback and subtle scan activity animation
- Drive capacity, used space, free space, accounted file allocation, and protected/system storage shown separately from the same scan snapshot
- Allocated size on disk for sparse and compressed files, with honest approximation markers when Windows cannot report allocation
- Categories for downloads, temporary files and caches, apps and games, Windows/system files, and personal files, plus independent screenshot and video filters
- Category, media, search, and size sorting controls that remain independent and composable
- Fast extended Windows directory scanning with direct file IDs/reparse tags, live files-per-second feedback, cancellation, background filtering/sorting, and a virtualized largest-files table
- A **Folders & games** view that totals every matching indexed file, detects common game/application install roots, and drills into ordinary folders without being limited to the 10,000 largest individual files
- Bounded scan, directory, cache, path-memory, and file-type aggregation workloads that stop safely instead of exhausting memory on adversarial or exceptionally large trees
- Standard unelevated scanning only in 1.6.2; the former **Full access / NTFS refresh (Administrator)** helper is temporarily disabled while it is replaced by a native broker
- Visible Normal, Important, and Protected safety notes plus a dedicated protected-item filter
- Validated completed-scan caching so the last results can be restored without rescanning at every launch
- Recycle Bin deletion only, with file-identity and stale-file validation plus stronger confirmation for sensitive locations
- Signed update manifests, bounded downloads, exact size and SHA-256 checks, a pinned GitHub release source, and user-controlled automatic update checks
- Upgrade-safe per-user installer with Desktop/Start Menu shortcuts, rollback, and Apps & Features uninstall support

## Understanding scan totals

Windows drive usage is authoritative. “Files accounted” is the allocated space represented by ordinary indexed files; sparse and compressed allocation is measured physically and hard-linked data is counted once. When Windows cannot expose an individual allocation, SpaceLens includes the explicit estimate and marks it with an asterisk instead of silently dropping it. “System / protected” is the remaining drive-used snapshot: NTFS metadata, restore points, reserved storage, protected locations, alternate streams, and similar Windows-managed data. It is not presented as directly deletable ordinary files, cannot honestly be converted into a complete delete queue, and cannot be guaranteed to reach zero.

SpaceLens uses decimal labels: `1 KB = 1,000 B`, `1 MB = 1,000 KB`, and `1 GB = 1,000 MB`.

## Finding large games and applications

The **Largest files** tab shows individual files and intentionally keeps only the 10,000 most relevant rows. A game can therefore use 80 GB without any one file being large enough to appear there. Open **Folders & games** to see recursive totals built from every matching indexed file. **Detected games & apps** groups common Steam, Xbox, Epic, GOG, EA, Ubisoft, Riot, WindowsApps, Program Files, and drive-root game libraries; **Browse scan root** covers custom folder layouts. Double-click a row to drill down. Folder rows are sorted by logical **File sizes** by default and also show unique physical **Size on disk**, which can differ for compressed, sparse, cloud, or hard-linked files.

## Safe cleanup behavior

SpaceLens never deletes a file automatically. Cleanup categories are review queues, not guarantees that every listed item is disposable. A deletion action always targets the selected path, revalidates its native metadata and identity, asks for confirmation, and uses the Windows Recycle Bin. Protected Windows/system files require an exact typed confirmation phrase; application and user-state files receive a visible Important warning. SpaceLens never takes ownership, rewrites ACLs, enables deletion privileges, or falls back to permanent deletion.

SpaceLens 1.6.2 keeps scanning, cleanup, updates, installation, and uninstall unelevated and rejects Administrator launches. It never requests UAC; cancel and report any unexpected SpaceLens elevation prompt. The former scan-only elevated helper is retired/temporarily unavailable pending a native broker design. A protected file can therefore be visible but remain non-recyclable when its Windows ACL denies the current app; SpaceLens reports that refusal instead of bypassing Windows protection.

## Install or run portable

On Windows 10 or 11 (64-bit):

1. Download `SpaceLens-Setup.exe` from the latest release.
2. Run it, review the privacy link and automatic-update option, and optionally create a Desktop shortcut.
3. Start SpaceLens, select a drive or folder, and choose **Scan now**. Version 1.6.2 deliberately offers only the standard unelevated scanner; Full access/NTFS refresh remains unavailable until a safer native broker is ready.

The release also contains a portable `SpaceLens.exe` that runs without installation. With no preference already saved for the same Windows account, a portable copy starts with automatic checks off; right-click **Check for updates** to enable or disable the at-most-daily automatic check. Installed and portable copies share that per-account preference. See the [privacy policy](PRIVACY.md) for the exact network behavior.

The current public builds are not Authenticode-signed, so Windows SmartScreen may show an unknown-publisher warning. The project is preparing for SignPath Foundation signing; the [code signing policy](CODE_SIGNING_POLICY.md) records the status, roles, provenance controls, and verification procedure without claiming that older unsigned binaries are signed. Even a future correctly signed build can initially receive a SmartScreen reputation warning while the new application establishes reputation.

SpaceLens 1.6.2 uses a validated v8 cache and automatically reclassifies compatible v7, v6, v5, v4, and v3 saved scans from earlier releases. Existing NTFS checkpoints are retained for compatibility, but 1.6.2 does not activate the elevated refresh helper. Folder/game totals work with a migrated completed scan; choose **Scan now** when the files on disk may have changed.

**1.6.1 security bootstrap:** the previous development signing key was retired and replaced with a new non-exportable, user-protected CNG key. Because 1.6.0 and earlier cannot securely authenticate this trust-anchor change, they must install 1.6.1 manually from the official GitHub release and verify its SHA-256 sidecar; do not rely solely on the old in-app update prompt for this transition. Later releases can again use the new key pinned by 1.6.1.

## Build from source

Install the .NET 10 SDK on Windows, then run:

```powershell
pwsh -File .\scripts\Build-Release.ps1
```

The version is read from `Directory.Build.props`. The default command builds and self-tests without accepting a private key. `-PrepareOfflineRelease` seals exact files and clean-commit provenance into a digest-addressed ZIP; the separate offline finalizer uses only a non-exportable, high-protection Windows CNG key plus an independently hashed/locked NativeAOT signer to create seven release assets, including provenance. No usable SignPath workflow currently exists. See [docs/PUBLISHING.md](docs/PUBLISHING.md).

## Project layout

- `src/SpaceLens` — analyzer and updater
- `src/SpaceLens.Setup` — per-user Windows installer and uninstaller
- `tools/ReleaseSigner` — manifest signing and verification utility
- `scripts/Build-Release.ps1` — private-key-free build, package, self-test, provenance, and hash workflow
- `scripts/Finalize-Release.ps1` — isolated offline manifest signing and atomic release assembly
- `scripts/Rotate-UpdateTrust.ps1` — one-shot user-session CNG trust rotation handoff
- `scripts/Build-SignPathRelease.ps1` and `Finalize-SignPathRelease.ps1` — retained experimental components; non-runnable until a split-job design is independently audited
- `signpath` — proposed metadata restrictions, not an enabled signing system
- `release-notes` — release history supplied with GitHub releases

## License

SpaceLens is available under the MIT License.

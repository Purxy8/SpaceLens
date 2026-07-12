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
- Optional **Full access / NTFS refresh (Administrator)** for local fixed drives, using a short-lived authenticated helper for broader protected coverage plus a choice between journal-based closed-file refresh and a complete rescan
- Visible Normal, Important, and Protected safety notes plus a dedicated protected-item filter
- Validated completed-scan caching so the last results can be restored without rescanning at every launch
- Recycle Bin deletion only, with file-identity and stale-file validation plus stronger confirmation for sensitive locations
- Signed update manifests, bounded downloads, exact size and SHA-256 checks, a pinned GitHub release source, and user-controlled automatic update checks
- Upgrade-safe per-user installer with Desktop/Start Menu shortcuts, rollback, and Apps & Features uninstall support

## Understanding scan totals

Windows drive usage is authoritative. “Files accounted” is the allocated space represented by ordinary indexed files; sparse and compressed allocation is measured physically and hard-linked data is counted once. When Windows cannot expose an individual allocation, SpaceLens includes the explicit estimate and marks it with an asterisk instead of silently dropping it. “System / protected” is the remaining drive-used snapshot: NTFS metadata, restore points, reserved storage, protected locations, alternate streams, and similar Windows-managed data. It is not presented as directly deletable ordinary files. Full access scan can turn more ACL-protected ordinary files into reviewable rows, but filesystem-managed storage cannot honestly be converted into a complete delete queue or guaranteed to reach zero.

SpaceLens uses decimal labels: `1 KB = 1,000 B`, `1 MB = 1,000 KB`, and `1 GB = 1,000 MB`.

## Safe cleanup behavior

SpaceLens never deletes a file automatically. Cleanup categories are review queues, not guarantees that every listed item is disposable. A deletion action always targets the selected path, revalidates its native metadata and identity, asks for confirmation, and uses the Windows Recycle Bin. Protected Windows/system files require an exact typed confirmation phrase; application and user-state files receive a visible Important warning. SpaceLens never takes ownership, rewrites ACLs, enables deletion privileges, or falls back to permanent deletion.

The elevated helper is scan-only; the main cleanup interface stays unelevated. A protected file can therefore be visible but remain non-recyclable when its Windows ACL denies the current app. SpaceLens reports that refusal and keeps the row instead of bypassing Windows protection.

## Install or run portable

On Windows 10 or 11 (64-bit):

1. Download `SpaceLens-Setup.exe` from the latest release.
2. Run it, review the privacy link and automatic-update option, and optionally create a Desktop shortcut.
3. Start SpaceLens, select a drive or folder, and choose **Scan now** for the faster standard Quick scan. Enable **Full access / NTFS refresh (Administrator)** when protected-location coverage is needed and approve the Windows UAC prompt with the same Windows account. After one saved full-access scan of an entire NTFS drive, SpaceLens offers a Fast NTFS refresh or a Complete rescan. Fast refresh follows closed-file journal changes and falls back when Windows exposes uncertainty; choose Complete rescan whenever continuously open logs/databases or an exact newly rebuilt snapshot matter.

The release also contains a portable `SpaceLens.exe` that runs without installation. With no preference already saved for the same Windows account, a portable copy starts with automatic checks off; right-click **Check for updates** to enable or disable the at-most-daily automatic check. Installed and portable copies share that per-account preference. See the [privacy policy](PRIVACY.md) for the exact network behavior.

The current public builds are not Authenticode-signed, so Windows SmartScreen may show an unknown-publisher warning. The project is preparing for SignPath Foundation signing; the [code signing policy](CODE_SIGNING_POLICY.md) records the status, roles, provenance controls, and verification procedure without claiming that older unsigned binaries are signed. Even a future correctly signed build can initially receive a SmartScreen reputation warning while the new application establishes reputation.

SpaceLens 1.6 uses a validated v7 cache with an optional NTFS change-journal checkpoint and can migrate v6, v5, v4, and v3 saved scans from earlier releases. Run one fresh whole-drive full-access scan after upgrading to enable later NTFS Quick Refresh.

## Build from source

Install the .NET 10 SDK on Windows, then run:

```powershell
pwsh -File .\scripts\Build-Release.ps1
```

The version is read from `Directory.Build.props`. Unsigned local build outputs are written to `artifacts/intermediate/`; no publishable release folder is created without the offline ECDSA update-manifest key. A manifest-signed, production-verified release contains exactly six upload assets in `artifacts/release/VERSION/`; Authenticode releases additionally follow the separate SignPath workflow. See [docs/PUBLISHING.md](docs/PUBLISHING.md).

## Project layout

- `src/SpaceLens` — analyzer and updater
- `src/SpaceLens.Setup` — per-user Windows installer and uninstaller
- `tools/ReleaseSigner` — manifest signing and verification utility
- `scripts/Build-Release.ps1` — reproducible build, package, self-test, and hash workflow
- `scripts/Build-SignPathRelease.ps1` — staged GitHub build for the two SignPath signing requests
- `scripts/Finalize-SignPathRelease.ps1` — offline provenance verification and final signed-update packaging
- `signpath` — versioned, metadata-restricted SignPath artifact configurations
- `release-notes` — release history supplied with GitHub releases

## License

SpaceLens is available under the MIT License.

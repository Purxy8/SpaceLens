# SpaceLens

SpaceLens is a friendly Windows disk-space analyzer. It scans a drive or folder, explains where the used space is going, and lets you review individual files before sending them to the Recycle Bin.

[Download the latest installer](https://github.com/Purxy8/SpaceLens/releases/latest/download/SpaceLens-Setup.exe) · [View all releases](https://github.com/Purxy8/SpaceLens/releases)

## Highlights

- Familiar decimal storage units (`B`, `KB`, `MB`, `GB`, and `TB`) throughout the interface
- A brighter, more polished layout with clearer status feedback and subtle scan activity animation
- Drive capacity, used space, free space, accounted file allocation, and protected/system storage shown separately from the same scan snapshot
- Allocated size on disk for sparse and compressed files, with honest approximation markers when Windows cannot report allocation
- Categories for downloads, temporary files and caches, apps and games, Windows/system files, and personal files, plus independent screenshot and video filters
- Category, media, search, and size sorting controls that remain independent and composable
- Fast buffered Windows directory scanning with live files-per-second feedback, cancellation, background filtering/sorting, and a virtualized largest-files table
- Optional **Full access scan (Administrator)** for local fixed drives, using a short-lived authenticated helper with Windows backup privilege for broader protected-location coverage
- Visible Normal, Important, and Protected safety notes plus a dedicated protected-item filter
- Validated completed-scan caching so the last results can be restored without rescanning at every launch
- Recycle Bin deletion only, with file-identity and stale-file validation plus stronger confirmation for sensitive locations
- Signed update manifests, bounded downloads, exact size and SHA-256 checks, and a pinned GitHub release source
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
2. Run it and optionally create a Desktop shortcut.
3. Start SpaceLens, select a folder on a local fixed drive, leave **Full access scan (Administrator)** enabled for broader protected-location coverage, and choose **Scan now**. Approve the Windows UAC prompt with the same Windows account. SpaceLens resolves directory-link roots to their canonical fixed-drive location; clear the option for network shares, removable drives, or a standard scan.

The release also contains a portable `SpaceLens.exe` that runs without installation. The current public builds are not Authenticode-signed, so Windows SmartScreen may show an unknown-publisher warning.

SpaceLens 1.5 uses a smaller and more strongly validated v6 cache and can migrate v5, v4, and v3 saved scans from earlier releases. Run one fresh full-access scan after upgrading for the most accurate current accounting.

## Build from source

Install the .NET 10 SDK on Windows, then run:

```powershell
pwsh -File .\scripts\Build-Release.ps1
```

The version is read from `Directory.Build.props`. Unsigned local build outputs are written to `artifacts/intermediate/`; no publishable release folder is created without the offline ECDSA signing key. A signed, production-verified release contains exactly six upload assets in `artifacts/release/VERSION/`. See [docs/PUBLISHING.md](docs/PUBLISHING.md).

## Project layout

- `src/SpaceLens` — analyzer and updater
- `src/SpaceLens.Setup` — per-user Windows installer and uninstaller
- `tools/ReleaseSigner` — manifest signing and verification utility
- `scripts/Build-Release.ps1` — reproducible build, package, self-test, and hash workflow
- `release-notes` — release history supplied with GitHub releases

## License

SpaceLens is available under the MIT License.

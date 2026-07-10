# SpaceLens

SpaceLens is a friendly Windows disk-space analyzer. It scans a drive or folder, explains where the used space is going, and lets you review individual files before sending them to the Recycle Bin.

[Download the latest installer](https://github.com/Purxy8/SpaceLens/releases/latest/download/SpaceLens-Setup.exe) · [View all releases](https://github.com/Purxy8/SpaceLens/releases)

## Highlights

- Familiar decimal storage units (`B`, `KB`, `MB`, `GB`, and `TB`) throughout the interface
- A brighter, more polished layout with clearer status feedback and subtle scan activity animation
- Drive capacity, used space, free space, indexed files, and unindexed filesystem overhead shown separately
- Allocated size on disk for sparse and compressed files, with honest approximation markers when Windows cannot report allocation
- Categories for downloads, temporary files and caches, apps and games, Windows/system files, and personal files, plus independent screenshot and video filters
- Category, media, search, and size sorting controls that remain independent and composable
- Responsive background scanning, filtering, sorting, cancellation, and a virtualized largest-files table
- Validated completed-scan caching so the last results can be restored without rescanning at every launch
- Recycle Bin deletion only, with file-identity and stale-file validation plus stronger confirmation for sensitive locations
- Signed update manifests, bounded downloads, exact size and SHA-256 checks, and a pinned GitHub release source
- Upgrade-safe per-user installer with Desktop/Start Menu shortcuts, rollback, and Apps & Features uninstall support

## Understanding scan totals

Windows drive usage is authoritative. “Indexed files on disk” is the known allocated space of ordinary files that SpaceLens could access. It can be lower than total used space because NTFS metadata, restore points, reserved storage, inaccessible locations, and some hard-link behavior cannot be represented as normal files. When Windows does not expose allocation for an individual file, SpaceLens marks its displayed size as approximate instead of silently counting it as zero. SpaceLens shows the remaining difference as unindexed/overhead instead of pretending it is deletable data.

SpaceLens uses decimal labels: `1 KB = 1,000 B`, `1 MB = 1,000 KB`, and `1 GB = 1,000 MB`.

## Safe cleanup behavior

SpaceLens never deletes a file automatically. Cleanup categories are review queues, not guarantees that every listed item is disposable. A deletion action always targets the selected path, validates that the file has not changed since the scan, asks for confirmation, and uses the Windows Recycle Bin. Windows and application locations receive an additional warning.

## Install or run portable

On Windows 10 or 11 (64-bit):

1. Download `SpaceLens-Setup.exe` from the latest release.
2. Run it and optionally create a Desktop shortcut.
3. Start SpaceLens, select a drive or folder, and choose **Scan now**.

The release also contains a portable `SpaceLens.exe` that runs without installation. The current public builds are not Authenticode-signed, so Windows SmartScreen may show an unknown-publisher warning.

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

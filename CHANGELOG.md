# Changelog

## 1.2.0 — 2026-07-10

- Replaced binary-looking `GiB`/`MiB` labels with familiar decimal `GB`/`MB`/`KB`/`B` units throughout the app.
- Refreshed the interface with modern metric cards, improved spacing and color, clearer status states, and subtle scan activity animation.
- Made unknown file allocation explicit instead of silently treating sparse, compressed, or inaccessible allocation as zero.
- Improved large-result filtering, sorting, category summaries, media detection, and multi-file removal performance.
- Strengthened scan traversal, cache identity and validation, cache-write ordering, and stale-file deletion checks.
- Hardened Setup version matching, upgrade/downgrade handling, shortcut preservation, rollback, self-tests, and uninstall cleanup.
- Tightened release packaging so the app itself production-verifies the signed update manifest and exact installer before publishing.

## 1.1.0 — 2026-07-10

- Added signed manual and daily background update checks.
- Added verified, bounded installer downloads and upgrade-aware Setup mode.
- Added cached completed scans and clearer live, partial, saved, and stale states.
- Improved scan accuracy for sparse files, long paths, cloud placeholders, and linked directories.
- Separated drive totals, indexed allocation, and filesystem overhead.
- Added composable category, media, search, and column-sort controls.
- Added screenshot/video and cleanup-candidate views.
- Moved filtering, sorting, grouping, and search off the UI thread.
- Added virtualized results, cancellation, crash logging, and deletion safety checks.
- Added a per-user installer, shortcuts, Apps & Features registration, and safe uninstall.

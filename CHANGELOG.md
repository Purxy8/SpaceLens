# Changelog

## 1.4.0 — 2026-07-11

- Added an optional full-access Administrator scan that keeps the UI unelevated, enables only Windows backup privilege in a short-lived authenticated helper, and preserves the fast buffered scanner.
- Preserved scan-time file attributes and added explicit Normal, Important, and Protected safety levels with a dedicated filter, visible safety notes, row styling, and per-file explanations.
- Added typed confirmation for protected Windows/system files while keeping Recycle Bin-only removal and allowing Windows to remain the final permission barrier.
- Replaced the legacy delete wrapper with Windows `IFileOperation` and `FOFX_RECYCLEONDELETE`, so cleanup fails safely when recycling is unavailable instead of silently falling back to permanent deletion.
- Fixed access-denied protected files being mistaken for missing files and silently removed from saved results.
- Fixed recycled allocations being incorrectly persisted as increased System / protected storage; drive accounting now requests a rescan after mutations.
- Added before/after drive-usage reconciliation notes, v5 saved-scan metadata with v4/v3 compatibility, a single-instance guard, and matching cache/protocol record limits.
- Expanded regression coverage for safety classification, protected filtering, native state validation, explicit confirmation, full-access metadata, and cache migration.

## 1.3.0 — 2026-07-10

- Replaced per-file handle probing with buffered Windows directory metadata scanning, dramatically improving scan throughput while retaining allocation size, timestamps, stable file IDs, sparse-file accounting, and hard-link deduplication.
- Added live files-per-second feedback and larger, async-safe UI batches so fast scans remain responsive.
- Fixed estimated allocations being excluded from drive and category totals, which made protected Windows files appear as unexplained missing space.
- Replaced the ambiguous unindexed/overhead metric with clearer files-accounted and system/protected storage metrics captured against the same drive snapshot.
- Added saved-scan summary metadata and backward-compatible v3 cache loading; older scans now request one refresh instead of showing a misleading residual.
- Prevented stale rows from being recycled while a new filter or sort view is still being built.
- Expanded scanner regression coverage for native-path availability, throughput, sparse files, hard links, reparse loops, long paths, cancellation, hostile entry validation, and cache migration.

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

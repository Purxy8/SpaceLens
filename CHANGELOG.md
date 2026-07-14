# Changelog

## Unreleased

## 1.6.2 — 2026-07-14

- Added a virtualized **Folders & games** view that aggregates every matching indexed file instead of relying on the 10,000-row largest-file display, so large installations split across thousands of smaller files show their complete totals.
- Added fast detection and one-click drill-down for Steam, Xbox, Epic, GOG, EA, Ubisoft, Riot, WindowsApps, Program Files, and common drive-root game libraries, plus ordinary scan-root folder browsing for custom locations.
- Kept category, media, search, and folder-size sorting fully composable; exact game-folder scans and cache/log filters now remain visible under the correct installation.
- Defaulted folder results to logical file-size totals while separately showing unique physical allocation, explicit estimates, and shared-hard-link ambiguity.
- Reduced folder-view UI work, bounded folder aggregation memory, cached recently detected install roots, and added 200,000-file performance coverage.
- Added v8 saved scans with automatic v7-and-older category/safety reclassification, clearer scan-coverage warnings, and safer cleanup-candidate precedence.
- Hardened cloud-placeholder traversal with a guarded reparse handle and same-volume identity validation, kept ordinary junctions/symlinks excluded, and restricted whole-volume shortcuts to verified local DOS drives.
- Expanded regression tests for an 80 GB multi-file game hidden from the largest-file list, exact-root scans, folder filter composition, false install markers, hard links, UNC roots, and legacy caches.

## 1.6.1 — 2026-07-13

- Retired and removed the obsolete development private key, completed a one-shot maintainer-run CNG trust rotation, and enrolled a new public key plus freshly signed self-test fixture. Upgrading from 1.6.0 or earlier requires one manual installer download from the official GitHub release.
- Split build/self-test and offline manifest signing into separate phases bound by a clean source commit, an independently copied prepared-ZIP digest, and exact per-input SHA-256/size provenance.
- Added support for a non-exportable, high-protection Windows CNG ECDSA P-256 signing key; the private key is never exported to a standalone file, and exportable private PEM commands hard-fail.
- Hardened ReleaseSigner with bounded strict inputs, P-256 enforcement, exclusive output creation, a single sign-and-public-key-verify operation, negative self-tests, and disabled .NET startup hooks.
- Removed all product-binary execution from the offline key account; the finalizer validates product bytes and launches only the independently hashed, locked NativeAOT signer.
- Pinned active GitHub Actions to reviewed full commit SHAs, removed persisted checkout credentials, retained least-privilege permissions, and retired the unsafe single-runner SignPath design in favor of an inert no-secret placeholder pending a separately audited split-job architecture.
- Added CI regression checks for action pinning, secret/PR isolation, workspace-local signing keys, exact-input hash rejection, and production apphost resistance to `DOTNET_STARTUP_HOOKS`.
- Temporarily disabled and retired the Full access/UAC helper pending a native broker; helper entry points fail closed while ordinary scanning, UI, cleanup, update, install, and uninstall stay unelevated and reject elevated operation.
- Hardened preserved scan protocol validation with root identity binding, cumulative IPC frame/byte/file/time budgets, and case-sensitive canonical containment.
- Tightened the updater to an exact HTTPS redirect allowlist with locked installer rehash-through-launch, and hardened Setup/uninstall staging, ownership, identity, and canonical path validation.
- Bounded live/cache results to 2 million files and 192 million path characters, bounded native/fallback directory traversal and file-type aggregation, and prevented oversized or adversarial trees from exhausting memory.
- Fixed managed fallback cancellation being swallowed as a completed partial scan, preserved the last partial batch when a traversal budget stops a scan, and narrowed broad filesystem catches so fatal failures propagate.
- Split the interactive network timeout into one minute for update checks and ten minutes for installer downloads.

## 1.6.0 — 2026-07-12

- Added conservative NTFS Quick Refresh after a saved whole-drive full-access scan: the elevated helper reads closed-file journal records, reopens every changed live file by stable ID, and automatically performs a complete scan when Windows reports detected uncertainty in the journal, directory tree, hard links, streams, identities, containment, or drive accounting. Because NTFS can coalesce writes while another application keeps a file open, the interface always offers a Complete rescan for an exact freshly rebuilt snapshot.
- Upgraded native enumeration to extended Windows directory records with direct reparse tags, lighter directory identity checks, a whole-volume canonical-path fast path, zero-copy batch handoff, and strict fallback for unsupported filesystems or 128-bit identities.
- Rewrote full-access IPC around pooled disposable frames and bounded span readers/writers, eliminating per-record streams and repeated UTF-8/path allocations while preserving authenticated parent-side canonical containment validation.
- Coalesced live progress and filter work, prevented UI callback backlog, showed results before cache compression finishes, added phase timings, reduced redundant cache/view passes, and clarified standard Quick scan versus Administrator full access.
- Added v7 saved scans with validated NTFS journal checkpoints and seamless v6/v5/v4/v3 compatibility; hostile, stale, partial, cross-volume, expired-journal, and oversized data still fail closed.
- Expanded protocol, cache migration, journal parser, reparse, hard-link, sparse-file, cancellation, allocation, packaged, and performance regression coverage.
- Added truthful privacy documentation plus install-time and in-app controls for automatic GitHub update checks; scan results and file information remain local.
- Prepared SignPath Foundation code signing with public policy/roles, metadata-restricted artifact configurations, code ownership, and a safety-locked two-stage GitHub-hosted workflow pending onboarding and full-SHA action pinning.
- Added an offline finalizer that verifies the workflow artifact ZIP digest, run/ref and source commit, signing-request IDs, hashes, product metadata, Authenticode signatures, and timestamps before signing the existing update manifest and assembling release assets.

## 1.5.0 — 2026-07-12

- Fixed the Full access UAC helper disconnect by keeping `CurrentUserOnly` on the protected pipe server while removing the incompatible elevated-client owner check; startup errors now return useful diagnostics instead of a generic closed-connection message.
- Reworked elevated scan transport around pooled, bounded 8,000-file frames and category codes, cutting per-file copies, retained duplicate strings, large-object churn, and UI progress backlog.
- Reduced scanner and filter allocation with single-pass classification, span-based filename/extension checks, lazy display names, value-type totals, v6 category-coded caches, and lower-memory rescans.
- Canonicalized scan roots through Windows handles and added same-handle directory identity/containment validation so junctions, aliases, mount points, and directory-swap races cannot escape the selected root.
- Hardened cleanup with final-path, metadata, safety, and file-ID revalidation immediately before each recycle-only Shell operation on a background STA worker; directories and ambiguous sensitive files fail closed.
- Added root file identity to v6 caches, v5/v4/v3 migration, alias migration, compressed/decompressed/text/count limits, directory-record rejection, and bounded state/manifest reads.
- Gated developer diagnostics out of production builds, rejected framework-dependent self-elevation, made release builds explicitly disable diagnostics, and expanded protocol, cache, reparse, updater, performance, and Recycle Bin regression coverage.

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

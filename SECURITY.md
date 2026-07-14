# Security policy

## Supported version

Security fixes are provided for the latest published SpaceLens version.

## Reporting a vulnerability

Please use GitHub's private vulnerability reporting or security-advisory feature for this repository. Do not include exploitable security details in a public issue.

Official in-app updates are accepted only when `update.json` has a valid signature from the public key embedded in SpaceLens and the downloaded installer matches the signed size and SHA-256 value. The pre-1.6.1 development key is retired and its obsolete private material was removed. A newly generated, non-exportable/user-protected CNG key and matching public fixture were enrolled for 1.6.1. Because an old client cannot securely authenticate a replacement key using only the old trust anchor, upgrading from 1.6.0 or earlier to 1.6.1 is a manual bootstrap from the official GitHub release.

SpaceLens 1.6.2 keeps the Full access/UAC helper disabled pending a native broker design. It never requests UAC; users should cancel and privately report any unexpected SpaceLens elevation prompt. Helper command-line and IPC entry points reject activation. Standard scanning, the UI, cleanup, updates, Setup, and uninstall remain unelevated and refuse elevated operation. Preserved protocol validation still binds root identity, uses cumulative frame/byte/file/time limits, and applies case-sensitive canonical containment.

Production apphosts disable managed startup hooks, and custom diagnostic command modes are compiled out; CI launches published SpaceLens with a harmless `DOTNET_STARTUP_HOOKS` probe and fails if it executes. CLR profiling or diagnostics forced externally can still influence a managed process before `Main`, especially if someone launches it elevated. This residual is why SpaceLens never initiates UAC in 1.6.2 and users must cancel unexpected prompts. The updater uses an exact HTTPS redirect-host allowlist, locked installer handles, and rehash-through-launch validation. Setup and uninstall use identity-bound staging and strict canonical path/ownership checks.

The SpaceLens 1.6.2 binaries are not Authenticode-signed and the per-user installation directory is user-writable. A SignPath Foundation application has been submitted, but no usable signing workflow exists: the previous design was retired because product execution and later secret access shared one runner. A future split-job/no-secret-product-execution design requires a new independent review. Until a release explicitly states that both files are signed, users should install only from this repository's Releases page and verify the published SHA-256 files manually.

Automatic update checks contact only the pinned GitHub release source and can be disabled during installation or from the update button's context menu. Scan data is not transmitted. Full details are in the [privacy policy](PRIVACY.md).

Release creation is split into a private-key-free build/self-test phase and an offline finalization phase bound to a clean commit, independently supplied archive and native-signer digests, exact per-file hashes, and a locked NativeAOT signer. Signing uses only a non-exportable, high-protection Windows CNG ECDSA P-256 key; exportable private PEM commands hard-fail. All product execution finishes before the key is accessed. `release-provenance.json` records source and artifact hashes.

The signed update schema authenticates the installer used for automatic updates. The portable executable is not an automatic-update payload; until Authenticode signing begins, its separate SHA-256 sidecar/provenance aid manual verification but are not a substitute for a detached cryptographic signature.

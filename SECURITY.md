# Security policy

## Supported version

Security fixes are provided for the latest published SpaceLens version.

## Reporting a vulnerability

Please use GitHub's private vulnerability reporting or security-advisory feature for this repository. Do not include exploitable security details in a public issue.

Official in-app updates are accepted only when `update.json` has a valid signature from the public key embedded in SpaceLens and the downloaded installer matches the signed size and SHA-256 value. The corresponding private key is stored offline and is never committed to this repository.

Full access scan uses a short-lived, scan-only Administrator helper. The ordinary UI and all cleanup operations remain unelevated; the helper authenticates both process IDs and a random nonce, validates every traversed directory on the same Windows handle, never changes ownership or ACLs, and exits after the scan. Production builds reject framework-dependent `dotnet.exe` self-elevation and compile out diagnostic file-output switches.

Current public binaries through v1.5.0 are not Authenticode-signed and the per-user installation directory is user-writable. The UAC prompt therefore provides consent but not an OS-verified publisher identity. SpaceLens is preparing a verifiable two-stage SignPath Foundation workflow that signs the app before embedding it into Setup and signs Setup afterward; see the [code signing policy](CODE_SIGNING_POLICY.md). Until a release explicitly states that both files are signed, users should install only from this repository's Releases page and verify the published SHA-256 files when downloading manually.

Automatic update checks contact only the pinned GitHub release source and can be disabled during installation or from the update button's context menu. Scan data is not transmitted. Full details are in the [privacy policy](PRIVACY.md).

# Security policy

## Supported version

Security fixes are provided for the latest published SpaceLens version.

## Reporting a vulnerability

Please use GitHub's private vulnerability reporting or security-advisory feature for this repository. Do not include exploitable security details in a public issue.

Official in-app updates are accepted only when `update.json` has a valid signature from the public key embedded in SpaceLens and the downloaded installer matches the signed size and SHA-256 value. The corresponding private key is stored offline and is never committed to this repository.

Full access scan uses a short-lived, scan-only Administrator helper. The ordinary UI and all cleanup operations remain unelevated; the helper authenticates both process IDs and a random nonce, validates every traversed directory on the same Windows handle, never changes ownership or ACLs, and exits after the scan. Production builds reject framework-dependent `dotnet.exe` self-elevation and compile out diagnostic file-output switches.

Current public binaries are not Authenticode-signed and the per-user installation directory is user-writable. The UAC prompt therefore provides consent but not an OS-verified publisher identity. A separately signed helper installed in an administrator-protected location is the recommended long-term trust-boundary improvement; users should install only from this repository's Releases page and verify published SHA-256 files when downloading manually.

# Publishing SpaceLens

## Prerequisites

- Windows with the .NET 10 SDK and PowerShell 7
- Git available on `PATH`
- Write access to `Purxy8/SpaceLens`
- The offline ECDSA P-256 private update-signing key
- Optional but recommended: an Authenticode code-signing certificate

Never commit or upload the private ECDSA key. The public verification key in `src/SpaceLens/assets/update-public-key.pem` is intentionally tracked.

## Prepare a version

1. Choose a strict `MAJOR.MINOR.PATCH` version.
2. Update `SpaceLensVersion` in `Directory.Build.props`; application, Setup, and file versions inherit it.
3. Add `release-notes/vVERSION.md` and update `CHANGELOG.md`.
4. Commit the intended source and confirm `git status --short` is empty.
5. Run the build with the offline key:

```powershell
pwsh -File .\scripts\Build-Release.ps1 `
  -SigningKey 'D:\secure\spacelens-update-private.pem' `
  -Notes 'Short update text displayed inside SpaceLens.'
```

The script reads the version from `Directory.Build.props`. `-Version` remains available as a guard for automation, but a supplied value must match that file exactly. Signed builds refuse a dirty or uncommitted source tree and report the exact Git commit used.

The script publishes self-contained x64 builds, runs both packaged GUI self-tests and checks their process exit codes, calculates hashes, creates and signs the update manifest, and verifies the signature with the tracked public key. It then invokes the packaged SpaceLens production verifier against the signed manifest and exact Setup binary. Only after every check succeeds does it create `artifacts/release/VERSION/`, containing exactly the six GitHub release assets. Unsigned CI/local runs stop after verified packaging in `artifacts/intermediate/` and do not create a final release directory.

## Code-signing order

If Authenticode signing is available, use this order:

1. Publish and Authenticode-sign `SpaceLens.exe`.
2. Build Setup with that exact application payload.
3. Authenticode-sign `SpaceLens-Setup.exe`.
4. Calculate Setup's final size and SHA-256.
5. Create and ECDSA-sign `update.json` using those final values.

Authenticode identifies the Windows publisher. The ECDSA manifest signature authenticates SpaceLens update metadata. They solve different problems and neither should be treated as a replacement for the other.

The included script performs the correct manifest-signing order for unsigned local builds. If Authenticode is introduced, add the signing commands before the final hash and manifest steps.

## Create the GitHub release

1. Create a draft release with tag `vVERSION`.
2. Use `release-notes/vVERSION.md` as the description.
3. Upload exactly:
   - `SpaceLens-Setup.exe`
   - `SpaceLens-Setup.exe.sha256`
   - `SpaceLens.exe`
   - `SpaceLens.exe.sha256`
   - `update.json`
   - `RELEASE-NOTES.md`
4. Verify every checksum and verify `update.json` with `tools/ReleaseSigner`.
5. Publish the release only after all files are present and correct.

Upload the six files directly from `artifacts/release/VERSION/`. Do not rename `SpaceLens-Setup.exe`; the updater deliberately rejects other installer names. Release binaries, hashes, and signed manifests belong in GitHub Releases, not in the source tree.

If the private key is lost, a new application build must pin a replacement public key. If the key is exposed, stop publishing immediately, revoke trust in the compromised key with a new application release, and rotate it.

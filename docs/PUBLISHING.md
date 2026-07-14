# Publishing SpaceLens

## Security prerequisites

- Windows, PowerShell 7, Git, and the pinned .NET 10 SDK
- A clean committed checkout of the exact release source
- A normal, unelevated, interactive maintainer account
- A non-exportable, high-protection Windows CNG ECDSA P-256 key

Never create or upload exportable private key material. The release tools hard-fail standalone/private-PEM key commands. Only the public verification key is tracked.

## One-time 1.6.1 trust reset

The previous development key is retired, and the maintainer completed this one-time handoff for 1.6.1. Do not publish from a source commit unless the matching public key and freshly signed fixture are committed and verified. Existing clients cannot securely authenticate a replacement key using the old trust anchor, so users of 1.6.0 or earlier must install 1.6.1 manually from the official release.

From the reviewed clean commit, build the NativeAOT signer to an otherwise empty external directory and record its SHA-256 through a separate trusted channel:

```powershell
dotnet publish .\tools\ReleaseSigner\ReleaseSigner.csproj `
  -c Release -r win-x64 --self-contained true -p:PublishAot=true `
  -p:DebugSymbols=false -o 'E:\trusted-tools\SpaceLens-1.6.1-signer'

$signer = 'E:\trusted-tools\SpaceLens-1.6.1-signer\ReleaseSigner.exe'
Get-FileHash -Algorithm SHA256 $signer

pwsh -File .\scripts\Rotate-UpdateTrust.ps1 `
  -ReleaseSigner $signer `
  -ExpectedSignerSha256 'SHA256_COPIED_SEPARATELY' `
  -CngKeyName 'SpaceLens Update Signing v2'
```

The signer directory must contain exactly `ReleaseSigner.exe`: no DLL, PDB, sidecar, runtimeconfig, deps file, `.local`, directory, or reparse point. The rotation script hashes and no-write/no-delete locks that exact native file, rejects agent/runner/service/elevated identities, creates the non-exportable key with explicit consent required on private-key use, signs and verifies a fresh `9.9.9` fixture, and transactionally replaces only:

- `src/SpaceLens/assets/update-public-key.pem`
- `src/SpaceLens/assets/update-selftest.json`

No executable runs after the rotation process exits. Review that exactly those two files changed, run the complete build/self-tests, and commit them. If the CNG key is lost, another manually bootstrapped trust reset is required.

## Build and seal release inputs

1. Update `SpaceLensVersion` in `Directory.Build.props`.
2. Update `CHANGELOG.md` and add `release-notes/vVERSION.md`.
3. Commit and confirm `git status --short` is empty.
4. Build without any private-key access:

```powershell
pwsh -File .\scripts\Build-Release.ps1 `
  -PrepareOfflineRelease `
  -Notes 'Short update text displayed inside SpaceLens.'
```

This phase publishes and self-tests SpaceLens and Setup, runs the harmless startup-hook rejection probe, builds/self-tests the NativeAOT signer, and records exact file hashes plus the source commit. It produces:

- `artifacts/intermediate/SpaceLens-release-input-vVERSION.zip`
- `artifacts/intermediate/native-release-signer/ReleaseSigner.exe`
- `artifacts/intermediate/ReleaseSigner.exe.sha256`

Copy the prepared ZIP and native signer to separate external locations. Copy both displayed digests independently; do not trust adjacent sidecars alone.

If the maintainer machine does not have the Visual C++ linker required for NativeAOT, run **Prepare verified release inputs** manually from the GitHub Actions page on the exact `main` commit. Paste that reviewed 40-character commit into `expected_source_sha`; the job fails if `main` resolves elsewhere. The workflow is restricted to the repository owner on `main`, has read-only repository permission, receives no configured repository or environment secrets, runs the same build and packaged self-tests, and uploads the prepared ZIP and its SHA-256 sidecar as two separately named three-day artifacts.

For each download, first verify the GitHub API's digest of the outer Actions artifact ZIP and require that wrapper ZIP to contain exactly one expected entry. Then hash the extracted inner prepared release ZIP and compare it with the sidecar extracted from the separately downloaded digest artifact; pass that inner ZIP digest to `Finalize-Release.ps1`. Apply the same outer-artifact-digest, exact-one-entry, and separate-sidecar checks to the NativeAOT signer artifacts from the canonical `Build and self-test` push run for the same source commit.

This workflow trusts the reviewed pinned GitHub actions, the exact .NET SDK version, and the GitHub-hosted Windows runner image. Separate artifacts and digests prevent accidental substitution and detect transport corruption; they are not an independent rebuild or a defense against a compromised hosted builder. A future release process can add a separately operated reproducible build comparison or artifact attestation without granting the product-build job any signing secret.

SpaceLens 1.6.2 keeps Full access/UAC disabled while a native broker is designed. The historical elevated probe is not applicable. Verify instead that helper entry points reject activation and that normal app, update, cleanup, install, and uninstall flows remain unelevated. Version 1.6.2 never requests UAC; cancel and report any unexpected prompt.

## Offline CNG finalization

Use a clean checkout of the same source commit. Put the independently authenticated native signer in an otherwise empty directory, then run:

```powershell
pwsh -File .\scripts\Finalize-Release.ps1 `
  -PreparedReleaseZip 'E:\incoming\SpaceLens-release-input-vVERSION.zip' `
  -ExpectedPreparedDigest 'PREPARED_ZIP_SHA256_COPIED_SEPARATELY' `
  -ReleaseSigner 'E:\trusted-tools\ReleaseSigner.exe' `
  -ExpectedSignerSha256 'SIGNER_SHA256_COPIED_SEPARATELY' `
  -CngKeyName 'SpaceLens Update Signing v2'
```

The finalizer never builds tools and never launches SpaceLens or Setup. Those untrusted product bytes were executed only in the earlier no-key build/CI environment. Finalization performs bounded exact ZIP extraction, verifies source provenance/hashes/sidecars, locks the independently hashed native signer, runs its self-test, signs through CNG, and independently verifies the exact P1363 manifest signature in-process. After CNG access it performs only in-process validation and atomic file operations.

The release directory contains exactly seven assets:

- `SpaceLens-Setup.exe`
- `SpaceLens-Setup.exe.sha256`
- `SpaceLens.exe`
- `SpaceLens.exe.sha256`
- `update.json`
- `RELEASE-NOTES.md`
- `release-provenance.json`

## SignPath status

No usable SignPath workflow exists for 1.6.2. The previous design mixed execution of repository-built product bytes with later secret access on one runner and was retired. `.github/workflows/sign-release.yml` is an inert literal-false placeholder with no checkout, third-party actions, or secrets.

SignPath approval alone is not permission to enable it. A future implementation requires a separate audit and three fresh jobs: a no-secrets/read-only build and self-test job producing a digest-bound artifact; a fresh secret-only signing job that never executes repository/product code and gives the token only to an immutable-SHA-pinned signing action; and a fresh no-secret validation job. Retained SignPath scripts/configurations are experimental and non-runnable until that design exists.

## Publish

Create a draft release targeting an already-pushed immutable `vVERSION` tag. Upload the seven files directly from `artifacts/release/VERSION/`, without renaming them. Verify both executable sidecars, independently verify `update.json` against the tracked public key, confirm provenance, then publish.

Release binaries and manifests belong in GitHub Releases, never the source tree. If the CNG key is lost or suspected compromised, stop publishing and perform another manual trust reset; an old client cannot securely authenticate that transition by itself.

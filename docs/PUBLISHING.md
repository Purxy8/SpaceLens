# Publishing SpaceLens

## Prerequisites

- Windows with the .NET 10 SDK and PowerShell 7
- Git available on `PATH`
- Write access to `Purxy8/SpaceLens`
- The offline ECDSA P-256 private update-signing key
- For an Authenticode release: an approved SignPath Foundation project, the SignPath GitHub App, a least-privilege submitter token, and the configured repository variables described below

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

The script publishes self-contained x64 builds, runs both packaged GUI self-tests and checks their process exit codes, calculates hashes, creates and ECDSA-signs the update manifest, and verifies the signature with the tracked public key. It then invokes the packaged SpaceLens production verifier against the signed manifest and exact Setup binary. Only after every check succeeds does it create `artifacts/release/VERSION/`, containing exactly the six normal release assets. These locally built executables are still **not Authenticode-signed**, even when the update manifest is ECDSA-signed, and must not be uploaded for a SignPath release. Unsigned CI/local runs stop after verified packaging in `artifacts/intermediate/` and do not create a final release directory.

Before a release that changes elevated scanning, also publish a temporary local diagnostic build with `-p:SpaceLensEnableDiagnostics=true` and run its `--integration-test-elevated-scan` probe from a medium-integrity shell against a disposable fixed-drive folder. The aggregate JSON report must show an unelevated parent, exactly one Ready event with backup privilege enabled, at least one file batch, and a successful final result. Never upload that diagnostic build; the signed production workflow explicitly forces `SpaceLensEnableDiagnostics=false`.

## SignPath Foundation signing

The manual `.github/workflows/sign-release.yml` workflow is deliberately separated from publication. Once enabled, it accepts only a fresh `vVERSION` tag, treated as immutable, whose value matches `Directory.Build.props`, and it uses two manually approved SignPath requests:

1. Build, upload, and Authenticode-sign `SpaceLens.exe` using the `spacelens-app-v1` artifact configuration.
2. Verify its trusted signature and packaged self-test, then calculate its final hash.
3. Build Setup with that exact signed application and sidecar embedded.
4. Upload and Authenticode-sign `SpaceLens-Setup.exe` using the `spacelens-setup-v1` artifact configuration.
5. Verify Setup's trusted signature and packaged self-test, then upload the signed artifact bundle.

The repository tracks the proposed artifact configurations under `signpath/`. After SignPath onboarding, create these repository settings:

- Secret: `SIGNPATH_API_TOKEN`
- Variables: `SIGNPATH_ORGANIZATION_ID`, `SIGNPATH_PROJECT_SLUG`, `SIGNPATH_SIGNING_POLICY_SLUG`, `SIGNPATH_APP_ARTIFACT_CONFIG`, and `SIGNPATH_SETUP_ARTIFACT_CONFIG`

The workflow is intentionally safety-locked with a literal `if: false` while the SignPath application is pending. Before enabling it, replace **every** `uses:` reference with the audited full commit SHA for that action, record the review in the release change, and only then remove the lock. Never enable mutable action tags for a workflow that receives the SignPath API token.

After onboarding and that security review, commit a clean source tree, create and push a fresh `vVERSION` tag for that commit, and treat it as immutable. Existing tags—including `v1.5.0`—must never be moved or reused. Run the workflow from the GitHub Actions page, select that exact tag, enter `VERSION`, and manually approve both signing requests in SignPath. Download the resulting **unmodified** `SpaceLens-SignPath-vVERSION` ZIP artifact outside the repository, and separately copy its SHA-256 and workflow run ID from the workflow summary. Check out the exact metadata source commit with a clean tree, then finish the release without uploading the offline update key:

```powershell
pwsh -File .\scripts\Finalize-SignPathRelease.ps1 `
  -SignedArtifactZip 'D:\downloads\SpaceLens-SignPath-vVERSION.zip' `
  -ExpectedArtifactDigest 'SHA256_FROM_GITHUB_WORKFLOW_SUMMARY' `
  -ExpectedWorkflowRunId 'GITHUB_WORKFLOW_RUN_ID' `
  -SigningKey 'D:\secure\spacelens-update-private.pem' `
  -Notes 'Short update text displayed inside SpaceLens.'
```

The finalizer first verifies the complete downloaded ZIP against the independently copied GitHub artifact digest. It accepts only the five expected flat files, validates both SignPath Foundation Authenticode signatures and timestamps, PE metadata, sidecar hashes, GitHub repository/run/ref/URL metadata, both signing-request IDs, and the exact clean local source commit. It reruns both packaged self-tests, creates the final hash sidecars, signs `update.json` with the offline ECDSA key, invokes SpaceLens's production update verifier, and atomically creates the normal six-file release directory. Keep both the downloaded ZIP and the private update key outside the repository.

Never store the offline ECDSA update key in GitHub or SignPath. Authenticode identifies the Windows publisher. The ECDSA manifest signature authenticates SpaceLens update metadata. They solve different problems and neither replaces the other.

## Code-signing order

Use this order for every Authenticode release:

1. Build, upload, and Authenticode-sign `SpaceLens.exe`.
2. Build Setup with that exact application payload.
3. Authenticode-sign `SpaceLens-Setup.exe`.
4. Calculate Setup's final size and SHA-256.
5. Create and ECDSA-sign `update.json` using those final values.

`Build-Release.ps1` remains the unsigned local/CI build and packaged self-test path. Do not use its unsigned binaries for an Authenticode release; use `sign-release.yml` followed by `Finalize-SignPathRelease.ps1`.

## Create the GitHub release

1. Create a draft release targeting the already-pushed `vVERSION` tag; do not ask GitHub to create or move the tag.
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

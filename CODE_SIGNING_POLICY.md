# Code signing policy

## Current status

SpaceLens has submitted an application to the SignPath Foundation open-source signing service and is awaiting acceptance. Until acceptance and the first signed release, GitHub release notes explicitly identify binaries as unsigned. No existing unsigned file is represented as signed.

For releases produced after acceptance:

**Free code signing provided by [SignPath.io](https://signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).**

The Authenticode publisher will therefore be SignPath Foundation. A valid signature proves that the binary was signed under that approved publisher identity and was not modified afterward. The separately verified GitHub workflow metadata and artifact digest bind the binary to its reviewed source and signing run. Neither check replaces normal review of what the application does.

Every active GitHub Actions `uses:` reference is pinned to a reviewed full commit SHA, and checkout credentials are not persisted. No usable SignPath workflow exists. The former single-runner design was retired because it mixed product execution with later token access; the remaining workflow is an inert literal-false placeholder with no checkout, actions, or secrets. A future split-job design requires a new independent review in addition to Foundation onboarding. Authenticode can remove the anonymous **Unknown publisher** identity, but SmartScreen may still warn while reputation develops.

## Source and signed artifacts

The canonical source repository is [Purxy8/SpaceLens](https://github.com/Purxy8/SpaceLens). Only these project-owned release binaries are in signing scope:

- `SpaceLens.exe`
- `SpaceLens-Setup.exe`

Signing is requested only for artifacts built from committed source by a GitHub-hosted GitHub Actions runner. `SpaceLens.exe` is signed first. Setup is then built with that exact signed application and its SHA-256 sidecar embedded, after which `SpaceLens-Setup.exe` is signed. The separately ECDSA-signed `update.json` is created offline only after the final signed Setup size and SHA-256 are known.

Manifest signing is isolated from builds and product execution. The only production key type is a non-exportable, high-protection current-user Windows CNG ECDSA P-256 key created by the maintainer from a normal interactive account, never an agent/runner/service identity. The old development key was retired; 1.6.1 must not be published until the maintainer completes the one-shot CNG rotation and commits only the new public key plus its freshly signed self-test fixture. Exportable private PEM commands hard-fail.

The SignPath artifact configurations restrict the file names and enforce `SpaceLens` product metadata plus the repository's centrally declared release version. Every Foundation signing request requires manual approval. Locally built, modified, diagnostic, or uncommitted binaries are never submitted for release signing.

## Team roles

SpaceLens currently has one maintainer:

- Author and committer: [Purxy8](https://github.com/Purxy8)
- Reviewer for outside contributions: [Purxy8](https://github.com/Purxy8)
- Release signing approver: [Purxy8](https://github.com/Purxy8)

The maintainer must use multi-factor authentication for both GitHub and SignPath. `CODEOWNERS` designates the maintainer to review changes to build scripts, GitHub workflows, signing configuration, this policy, and the privacy policy. If branch protection or the team changes, these roles and enforced approval permissions will be updated before new members participate in signing.

## Privacy and verification

SpaceLens's network behavior and opt-out controls are documented in the [privacy policy](PRIVACY.md). The project never uploads scan results or file information.

After signed releases begin, users can inspect a downloaded file in Windows **Properties > Digital Signatures**, or run:

```powershell
Get-AuthenticodeSignature .\SpaceLens-Setup.exe | Format-List Status,StatusMessage,SignerCertificate,TimeStamperCertificate
```

Release SHA-256 sidecars and the independently signed update manifest remain in place in addition to Authenticode.

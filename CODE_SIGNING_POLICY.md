# Code signing policy

## Current status

SpaceLens is preparing to apply for the SignPath Foundation open-source signing service. Until acceptance and the first signed release, the GitHub release notes will explicitly identify binaries as unsigned. No existing unsigned file is represented as signed.

For releases produced after acceptance:

**Free code signing provided by [SignPath.io](https://signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).**

The Authenticode publisher will therefore be SignPath Foundation. A valid signature proves that the binary was signed under that approved publisher identity and was not modified afterward. The separately verified GitHub workflow metadata and artifact digest bind the binary to its reviewed source and signing run. Neither check replaces normal review of what the application does.

The signing workflow remains disabled until Foundation onboarding is complete and every GitHub Actions `uses:` reference is pinned to an audited full commit SHA. Authenticode removes the anonymous **Unknown publisher** identity, but Windows SmartScreen can still show a reputation warning for a new certificate or newly released application until reputation develops.

## Source and signed artifacts

The canonical source repository is [Purxy8/SpaceLens](https://github.com/Purxy8/SpaceLens). Only these project-owned release binaries are in signing scope:

- `SpaceLens.exe`
- `SpaceLens-Setup.exe`

Signing is requested only for artifacts built from committed source by a GitHub-hosted GitHub Actions runner. `SpaceLens.exe` is signed first. Setup is then built with that exact signed application and its SHA-256 sidecar embedded, after which `SpaceLens-Setup.exe` is signed. The separately ECDSA-signed `update.json` is created offline only after the final signed Setup size and SHA-256 are known.

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

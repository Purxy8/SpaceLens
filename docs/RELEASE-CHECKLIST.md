# Release checklist

- [ ] Version is strict `MAJOR.MINOR.PATCH` everywhere.
- [ ] Release notes and changelog are updated.
- [ ] Application and Setup publish successfully with zero build errors.
- [ ] Application packaged self-test passes.
- [ ] Setup packaged self-test passes.
- [ ] Diagnostics single-file probe passes a real medium-integrity-to-UAC Full access scan: exactly one Ready=true event, at least one file batch, and backup privilege enabled.
- [ ] Full access smoke test is repeated from installed and portable locations; canceling UAC preserves prior results.
- [ ] Recycle Bin integration test passes for multiple disposable files.
- [ ] Normal/minimized startup smoke test passes.
- [ ] Setup embeds the exact intended application payload.
- [ ] Final executable hashes match their `.sha256` files.
- [ ] `update.json` contains Setup's exact final size and SHA-256.
- [ ] `update.json` verifies with the tracked public key.
- [ ] No private key, secret, local path, log, cache, or generated binary is staged in Git.
- [ ] Production package was built with `SpaceLensEnableDiagnostics=false`; diagnostic command switches are unavailable.
- [ ] Draft GitHub release contains all six required assets.
- [ ] Latest-download links work before announcing the release.

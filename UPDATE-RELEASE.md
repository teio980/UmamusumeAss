# Update release and signing runbook

The updater trusts only the public key embedded in the WPF verifier and the
native updater. `UpdateSigner` signs the exact UTF-8 bytes of a deterministic
manifest with ECDSA P-256/SHA-256 and writes a base64-wrapped 64-byte IEEE
P1363 `r||s` signature. The private key must never be committed, uploaded as an
artifact, or printed in CI logs.

## Bootstrap and key setup

`v0.2.0` is the first updater-capable release. Versions older than it must be
installed manually once; they cannot safely migrate their installation tree.

Before publishing `v0.2.0`, generate a production key on an offline machine:

```powershell
dotnet run --project tools/UpdateSigner/UpdateSigner.csproj -- --generate-key C:\secure\uma-update-private.pem C:\secure\uma-update-public.txt
```

Replace the development fixture public key in `ManifestVerifier.cs`,
`src/UmamusumeAssUpdater/main.cpp`, and `tools/UpdateSigner/Program.cs` with
the generated SPKI base64 value. Run the update tests and native build before
publishing. Store the PEM as the `UMA_UPDATE_SIGNING_KEY_PEM` secret in the
protected GitHub Environment named `update-signing`; require a human approval
for that environment. The release workflow is `workflow_dispatch` only and
creates draft program and resource releases after tests. A maintainer must
review the draft assets/manifests/signatures and publish them manually. If the
GitHub UI/CLI cannot promote a draft in the current policy, use the Release UI
or an explicitly approved `gh release edit <tag> --draft=false` step.

For key rotation, generate a new key, update all three embedded public-key
locations in one code release, update the protected secret, and publish that
release as the first version signed by the new key. Do not rotate only one
location: old binaries intentionally stop trusting manifests signed by a key
they do not embed. Keep the old key private and archived according to the
project's secret-retention policy, but never place it in this repository.

There is one important one-release bootstrap boundary: an installation whose
WPF executable already trusts the new key but whose installed native helper
still embeds the old key cannot repair itself if that WPF executable predates
the staged-helper handoff. The old helper will reject the new-key manifest, and
one ECDSA signature cannot satisfy both keys. Do not weaken verification by
accepting an untrusted fallback key. Recover such an installation by closing
the app and installing the complete portable package for the repaired release
(or replacing the helper from that package after independently verifying the
release manifest and package hash); the next in-app update then starts the
helper copied from the already verified payload. This limitation is deliberate
and should be called out in release notes whenever a signing key is rotated.

## Manifest/package contract

Program tags are exactly `vX.Y.Z`; resource tags are exactly
`resource-vYYYY.MM.DD.N`. The client filters both namespaces independently
from GitHub's releases API and never uses `/releases/latest`. Manifest assets
contain an `assetName`, not an arbitrary URL; the client derives the URL from
the fixed `teio980/UmamusumeAss` repository and allows only GitHub HTTPS
redirect hosts.

Every program release includes a complete portable ZIP (including its bundled
`resource/` fallback and updater), a per-user Windows installer, and direct
deltas from up to the three latest stable program releases. The installer is
built with Inno Setup and registers an uninstall entry in Windows; it installs
the same files as the portable ZIP under %LOCALAPPDATA%\Programs and leaves
user settings intact when uninstalled. Resource ZIPs contain the *contents* of `resource/`
at their archive root (`foo/bar`, not `resource/foo/bar`), matching the
resource base tree and inventory hash. Resource releases also carry direct
deltas from up to three stable resource revisions. Any delta mismatch or
apply-time failure falls back to a complete package on the next attempt.

The local staging path retains the original signed manifest bytes and
signature. It requires the ZIP's regular-file set to exactly equal the signed
asset inventory, validates every file SHA-256, rejects path traversal,
absolute paths, ADS, reparse escapes, and enforces the 10,000-file/1 GiB
single-file/2 GiB expanded-size limits. Unknown files in an existing portable
installation are never deleted. The external updater independently verifies
the selected asset, old signed inventory/tree for deltas, target inventory/tree,
and its journal before replacement.


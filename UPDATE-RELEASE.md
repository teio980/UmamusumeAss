# Update release and signing runbook

The updater trusts the P-256 public key in the repository's `update-public.txt`.
`Umamusume.UpdateCore` embeds that single source in the WPF updater, macOS
application, macOS helper, and `UpdateSigner`. The protected GitHub Environment
`update-signing` supplies the matching `UMA_UPDATE_SIGNING_KEY_PEM` secret to the
draft-release workflow. Never commit, upload, or print the private key. Keep the
existing production key; changing it requires a separately planned rotation
release that updates the protected secret and every client that embeds the
public key. The Windows native updater stores the corresponding P-256
coordinates; its test compares them with the same `update-public.txt` key.

## First Mac installation and supported updates

Install the first macOS release that contains `Umamusume.MacUpdater` by
replacing the existing `.app` manually. Earlier Mac builds do not include the
helper needed to perform a safe in-place update. Future macOS program updates
are ARM64-only, require macOS 14 or newer, and use a complete `.app` ZIP. They
do not use differential packages. The user downloads the package and clicks
**Restart to install**; the signed package is staged and checked before the app
closes. If the app bundle is not writable or its volume cannot atomically swap
directories, move the app to `~/Applications` and retry. The updater never asks
for administrator access.

The helper is copied from the verified package to a per-user operation folder
before the old app exits. It verifies the signed inventory, prepares a complete
candidate beside the installed bundle, checks the ad-hoc code signature, then
uses a same-volume atomic directory swap. The candidate must open a window,
initialize the native core, and load the resource catalog before the helper
commits the update. A failed launch or health check restores the previous app.
The helper keeps the most recent successful backup. App data and preferences
remain in the user's Application Support directory.

Resource updates are independent from program updates. The user downloads a
signed full resource archive and clicks **Apply resources** to activate it. The
native core reloads the candidate and validates its catalog before the active
resource pointer changes; failure restores the old workspace and pointer.
Resource overrides stay in the per-user data directory and are copied into the
new view. The bundled resource tree remains the fallback if no compatible,
verified revision is available.

In **Settings → Updates**, **Delete old resources** removes downloaded but
unstaged or uninstalled update packages, updater check caches, inactive resource
revisions, and inactive runtime views. It waits for native work to become idle
and can be canceled while it runs. It keeps the active resource revision and
view, settings, logs, resource overrides, drafts, the app rollback backup, and
any operation still needed for installer recovery. If an install/recovery
helper is running or the active-resource pointer is invalid, the cleaner keeps
the affected caches conservatively. It only operates inside the app's managed
user-data directories and does not clear the user's global .NET cache.

## Manifest and package contract

Program tags are exactly `vX.Y.Z`; resource tags are exactly
`resource-vYYYY.MM.DD.N`. Windows retains its existing full and differential
manifest protocol. macOS uses a platform-specific signed manifest and full
asset only:

| Scope | Manifest | Signature | Full package |
| --- | --- | --- | --- |
| Program | `osx-arm64-program-manifest-{version}.json` | Same stem with `.sig` | `UmamusumeAss-v{version}-osx-arm64.app.zip` |
| Resources | `osx-arm64-resource-manifest-{revision}.json` | Same stem with `.sig` | `UmamusumeAss-resource-v{revision}-osx-arm64-full.zip` |

Mac manifests use schema 1, `targetOs: macos`, `targetArch: arm64`, and
`minOsVersion: 14.0`. Resource manifests include app-version and resource
schema compatibility. The release workflow currently creates draft releases;
maintainers must review the platform assets, manifest inventories, and
signatures before publishing. No release is published automatically.

Windows continues to publish its full portable ZIP and installer and can also
publish signed direct deltas from up to three recent stable releases. Windows
delta mismatches fall back to a full package. The platform-specific Mac
manifest prefix keeps Windows manifest discovery from selecting Mac assets.

Resource archives contain the contents of `resource/` at the archive root
(`foo/bar`, not `resource/foo/bar`). The Mac resource asset can reuse those
same resource bytes under its platform-specific asset name.

The Mac package preserves the project's ad-hoc `codesign` mode. It is signed
before ZIP creation, then the exact archive bytes are hashed into the signed
manifest. The release flow does not use Developer ID credentials or notarize
the package.

## Creating and reviewing a release draft

Use the `Release` workflow's manual dispatch. Set `version` to the exact
`version.txt` value and `bundled_resource_version` to the resource revision
included with the program package. For a resource or combined release, also
set `resource_version`. The workflow builds and tests on `windows-2022` and
`macos-15`; the Mac CMake deployment target is 14.0. It signs manifests with
the protected `update-signing` environment and creates draft releases only.

Before publishing a draft, verify that each platform-specific program manifest
names its corresponding full package, and that the Mac manifest contains no
delta assets. Check the resource platform manifests and their full package
names, the signed file inventories and hashes, the app's
`LSMinimumSystemVersion`, and that no bundled Mach-O requires a macOS version
newer than 14.0.
Publish only after these checks pass. `build.yml` runs the same managed updater
tests and creates a short-lived CI artifact; its `dist/` package is not a
release.

## Verification details

The local stager retains the original signed manifest bytes and signature. It
requires the ZIP's regular-file set to exactly equal the signed inventory,
validates every file SHA-256, rejects traversal, absolute paths, symlinks,
special files, and Mac case/Unicode path collisions, and enforces the
10,000-file, 1-GiB per-file, and 2-GiB expanded-size limits. Mac app replacement
preserves executable modes. Resource revisions are immutable and independently
verified before activation. Recovery uses a persistent journal and compares
the installed and candidate bundle versions to detect a swap completed before
the journal was advanced.

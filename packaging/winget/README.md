# winget packaging — submission approved by SEAN 2026-10-25

This folder holds a legacy [winget](https://learn.microsoft.com/windows/package-manager/) manifest set for MuseDesktop. Current support is Windows 11 x64 only; ARM64 has been removed and the minimum OS is build 22000. Before any submission, replace the legacy release metadata with a verified current release:

| File | Contents |
| --- | --- |
| `SeanShaffer88.MuseDesktop.yaml` | Version manifest (identifier + version + default locale) |
| `SeanShaffer88.MuseDesktop.installer.yaml` | x64 installer only, Windows 11 minimum, type `nullsoft`, per-user scope |
| `SeanShaffer88.MuseDesktop.locale.en-US.yaml` | Publisher, name, license, descriptions |

The set still describes historical **v1.0.0**, not the current release, with its real x64 installer URL and hash. It must be refreshed and revalidated before submission; no new hash is invented before release assets exist.

**Status: submission approved.** SEAN gave the go-ahead on 2026-10-25; the submission PR to `microsoft/winget-pkgs` follows the steps below. Until it is merged there, `winget install MuseDesktop` does not exist; the Releases page is the way to get the app.

## Submitting (when the go-ahead is given)

1. Fork `https://github.com/microsoft/winget-pkgs` and clone your fork.
2. Copy the three YAMLs into the fork at:
   `manifests/s/SeanShaffer88/MuseDesktop/1.0.0/`
   (path rule: `manifests/<first letter, lowercase>/<PackageIdentifier>/<PackageVersion>/`).
3. Validate locally (on Windows, with winget installed):
   - `winget validate --manifest manifests/s/SeanShaffer88/MuseDesktop/1.0.0`
   - Test-install: `winget install --manifest manifests/s/SeanShaffer88/MuseDesktop/1.0.0` and confirm the app installs per-user, launches, and uninstalls cleanly.
4. Commit on a branch named `SeanShaffer88.MuseDesktop-1.0.0`, push to your fork, and open a PR against `microsoft/winget-pkgs` master titled `New package: SeanShaffer88.MuseDesktop version 1.0.0`.
5. Respond to the winget bot / reviewer feedback (validation runs in their pipeline; label and content policies are theirs — e.g. they may ask for `PublisherUrl` support page changes or tag additions).

Alternative: `wingetcreate` (`winget install Microsoft.WingetCreate`) can generate the set from the installer URL — the files here are equivalent and already filled in, so the manual copy above is the shorter path.

## Per-release updates (after the first submission is live)

Every release (see `RELEASE_CHECKLIST.md`):

1. In this folder, copy the three YAMLs' values forward: bump `PackageVersion` in all three, replace the x64 `InstallerUrl` and `InstallerSha256` with the new release's (from its `SHA256SUMS.txt`, uppercase hex), keep the Windows 11 minimum OS, and update `ReleaseDate`.
2. Submit the same way, into `manifests/s/SeanShaffer88/MuseDesktop/<new version>/` in a new PR. Updates are a new version folder; never edit an already-published version's folder except through a winget-pkgs correction PR.
3. The hashes MUST come from the published release assets (download them back and re-hash, or use the release's `SHA256SUMS.txt` after verifying it against the assets — the checklist's existing rule).

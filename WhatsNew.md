# What's new

The release notes MuseDesktop shows on the first launch of a new version (S9 #82). This file is generated from CHANGELOG.md's current-version section at release time (see RELEASE_CHECKLIST.md) — do not edit it by hand; a test pins it to the changelog so the two can't drift.

## [0.13.0] — Unreleased

Slice 9 (items 81–88) of the 100-enhancement program: updates, installer & distribution.

### Added

- **In-app update download with hash verification (#81)** — the update notice now opens an update window instead of only sending you to the browser: **Download update** fetches the installer for your PC (x64 or ARM64) with percent + speed + ETA, then checks its SHA-256 fingerprint against the release's `SHA256SUMS.txt`. Only a verified installer can be installed, and only when you choose **Install** — nothing is ever downloaded or installed automatically or silently. A fingerprint mismatch deletes the file with a plain explanation; a release without a sums file is refused (this project's releases always include one).
- **What's New (#82)** — the first launch of a new version shows that version's release notes once. Fresh installs don't see it (there's nothing "new" yet), a "Don't show What's New again" box and a Settings switch turn it off, and it never appears before the app-lock password.
- **Update snooze + check interval (#83)** — automatic checks can run every launch (as before), daily, weekly, or never (manual only; the ⋯ menu's *Check for updates* always works). The update window's **Remind me tomorrow** / **Remind me next week** snooze reminders; Settings → Behavior shows an active snooze and can cancel it.
- **Rollback (#84)** — the in-app updater keeps the installer it downloaded; when the next update installs, that installer becomes the rollback: **Diagnostics** gains **Reinstall previous version (x.y.z)**, which runs it in place after a confirm. Honest coverage: rollback exists only for versions that were themselves reached through the in-app updater (the chain starts with the first update after this version) — versions installed from the website or a zip have no kept installer. Exactly one previous version is kept.
- **winget manifest, prepared (#85)** — `packaging/winget/` now holds a ready manifest set for the current published release (v0.5.0, both architectures, with the real installer hashes) plus the exact submission steps. The submission itself to microsoft/winget-pkgs happens only with the maintainer's explicit go-ahead — until then, `winget install MuseDesktop` is **not** live; the installer/zip on the Releases page remain the way to get MuseDesktop.
- **Release channel (#88)** — Settings → Behavior: **Stable releases only** (default) or **Stable + pre-releases**, with the warning spelled out. Stable users are never offered a pre-release; if you try pre-releases and switch back, you're offered the next stable release — never an automatic downgrade.

### Changed

- **Installer audit (#86)** — the installer already offered a folder choice and already reused your folder on upgrade; what was missing was memory for the **Desktop shortcut** checkbox (optional, off by default). The installer now records your choice and pre-selects it on the next install/upgrade, so an upgrade no longer second-guesses it.

### Notes

- **Code signing (#87)** — still **unsigned**: the certificate is the maintainer's purchase decision, and none was made. What this release adds is readiness: a manually-triggered `Sign-Release` workflow that stays inert until two repository secrets and one repository variable exist, plus the enable steps in `RELEASE_CHECKLIST.md`. The day a certificate exists, signing turns on without a code change — and until then SmartScreen's first-run warning remains (see KNOWN_LIMITATIONS).
- Core test suite grows by 44 cases (461 → 505). All v0.13.0 runtime behavior is **Unverified** on a real Windows machine pending the `RELEASE_CHECKLIST.md` S9 pass — in particular the full update loop can only be exercised once a NEWER release exists to update to (see the checklist for what can be checked now vs then).

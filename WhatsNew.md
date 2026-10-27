# What's new

The release notes MuseDesktop shows on the first launch of a new version (S9 #82). This file is generated from CHANGELOG.md's current-version section at release time (see RELEASE_CHECKLIST.md) — do not edit it by hand; a test pins it to the changelog so the two can't drift.

## [1.8.4] — Unreleased

Follow-on slice R7D (directed 2026-10-27): seamless updates.

### Changed

- **Updating is hands-free after you accept.** You still get
  the say: the update window offers the new version, you
  choose Download, the installer is checked against the
  release's published fingerprint, and you choose Install.
  From that click on, there's nothing else to do — you don't
  have to close MuseDesktop first or click through a setup
  wizard. The installer closes the app if it's running
  (even hidden in the tray, even locked), installs the new
  version over the old one, and reopens MuseDesktop when
  it's done. Your sign-in and settings carry over, as
  always. Rollback is unchanged: reinstalling the previous
  version from Diagnostics keeps the classic installer
  wizard and doesn't reopen the app for you.

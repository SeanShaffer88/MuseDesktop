# What's new

The release notes MuseDesktop shows on the first launch of a new version (S9 #82). This file is generated from CHANGELOG.md's current-version section at release time (see RELEASE_CHECKLIST.md) — do not edit it by hand; a test pins it to the changelog so the two can't drift.

## [1.8.5] — Unreleased

Hotfix R7D1 (directed 2026-10-27): the installer close/relaunch
fix for R7D, from SEAN's field test of v1.8.4.

### Fixed

- **The installer now really closes MuseDesktop before
  installing.** The v1.8.4 installer could fail to notice
  MuseDesktop was running, so it didn't close it before
  installing — it now checks for the running program
  directly, asks it to close (and makes it close if it
  won't), and only then copies the new files. Reopening is
  the **Run MuseDesktop** box on the installer's last page:
  the app starts as you close the installer, and only if
  the box is ticked. When MuseDesktop was running, the
  box starts out ticked so the app comes back — untick it
  and it stays closed. Installing from inside the app is
  unchanged: no installer window appears, and the app
  closes, updates, and reopens by itself.

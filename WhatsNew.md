# What's new

The release notes MuseDesktop shows on the first launch of a new version (S9 #82). This file is generated from CHANGELOG.md's current-version section at release time (see RELEASE_CHECKLIST.md) — do not edit it by hand; a test pins it to the changelog so the two can't drift.

## [1.8.8] — Unreleased

Follow-on hotfix R7E2 (directed 2026-10-27): the Settings
topics rail, made readable — from SEAN's field test of
v1.8.6.

### Fixed

- **The Settings topics list reads clearly again.** The
  topic names and icons down the left side of Settings were
  painted black on the near-black rail in the dark theme —
  a built-in Windows list default quietly overrode the
  window's text color for that one list, so the topics you
  hadn't picked were almost invisible. The rail now uses
  the theme's text color like the rest of the window:
  white in the dark theme, dark in the light theme, in
  every state — picked, hovered, or waiting.

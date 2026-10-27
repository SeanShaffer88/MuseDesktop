# What's new

The release notes MuseDesktop shows on the first launch of a new version (S9 #82). This file is generated from CHANGELOG.md's current-version section at release time (see RELEASE_CHECKLIST.md) — do not edit it by hand; a test pins it to the changelog so the two can't drift.

## [1.8.2] — Unreleased

Follow-on slice R7B (directed 2026-10-27): a token-usage ring
in the main window, and the Bookmarks button's move to the
upper-right corner.

### Added

- **Token-usage ring.** A circular ring in the main window's
  upper-right corner, immediately left of the zoom controls,
  shows how full the current conversation's context is, with
  the percent in its center (a ~ marks an estimate). When the
  Muse page itself displays a usage figure, the ring reads
  that; when it doesn't, the ring estimates from the
  conversation's length and says so — in the ring, its
  tooltip, and its panel. Clicking the ring opens a small
  panel with the counts, the source, and a Refresh button.
  The estimate's limit is a new Settings → Appearance &
  toolbar value (default 200,000 tokens), used only in
  estimate mode.
- **Bookmarks moved.** The Bookmarks button now sits in the
  upper-right cluster, left of the token ring, instead of on
  the left rail.

### Removed

- **The left rail is gone.** The slim left strip added in
  v1.6.0 held only the Bookmarks button; with the button
  moved, the rail is removed and the conversation reclaims
  the full window width.

### Changed

- **The titlebar says "MuseDesktop" — and nothing else.** No
  page title, no profile name; just the app's name, so the
  window is instantly recognizable in the taskbar and task
  switcher. (Secondary windows keep their conversation
  titles, so they stay tellable apart.)

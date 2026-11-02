# What's new

The release notes MuseDesktop shows on the first launch of a new version (S9 #82). This file is generated from CHANGELOG.md's current-version section at release time (see RELEASE_CHECKLIST.md) — do not edit it by hand; a test pins it to the changelog so the two can't drift.

## [2.0.0] — Unreleased

### Added

- **Compact Focus shell layout.** Settings → Appearance gains a
  **Shell layout** picker: **Standard** (the classic toolbar and
  full status bar) or **Compact Focus** — a slim 30px toolbar, and
  the status bar replaced by a small floating pill in the page's
  bottom-right corner carrying the same status line, so the page
  gains 34px of height at comfortable density (22px at compact).
  The same switch lives in the ⋯ menu ("Compact Focus layout") and
  the command palette ("Toggle Compact Focus layout"), the choice
  is remembered, and focus mode (F8) still hides all chrome and
  restores the layout's own chrome on exit. Shell only, as always:
  the Muse page is never restyled.

- **Left Rail shell layout.** A third choice for the
  **Shell layout** picker (Settings → Appearance, the ⋯ menu, and
  the command palette, which now cycles through all three): a
  56px rail down the left edge carries Back, Forward, Reload,
  Home, Downloads, the always-on-top pin and Open in browser,
  with Settings and the ⋯ menu at the rail's foot. Across the
  top sits a thin 30px title strip — page title and zoom
  controls — and the status bar stays along the bottom, so the
  status line keeps its full width. The rail's buttons do
  exactly what their toolbar equivalents do, history menus
  included, and focus mode (F8) hides the rail with the rest of
  the chrome and restores it on exit. Shell only, as always:
  the Muse page is never restyled.

- **Page health check in Diagnostics.** A "Run page check"
  button probes the loaded Muse page for the markers the
  page-based features rely on and lists a verdict for each:
  message bookmarks, drafts, conversation export, and approval
  alerts. The probe runs the same detectors the features
  themselves use and reports only whether each marker is there —
  never any page content — and an approval watcher with no card
  to watch is reported as exactly that, not as a failure. If the
  page's shape changes and a feature would quietly stop working,
  the check says so instead.

- **Guided field pass in Diagnostics.** A "Field pass" section
  in Diagnostics (and a ⋯ menu entry) opens a guided checklist
  of nine checks that prove the app on a real PC: the shell
  layouts, the page health check, the approval alert,
  conversation tools, downloads, the tray and hotkey, the app
  lock, Settings search, and the update check. Each check says
  what to do and what working looks like, with a button that
  opens the feature where one exists; you mark each Pass, It
  has a problem, or Skip for now, with an optional note. The
  window stays open alongside the app while you work, answers
  save as you go on this PC — closing the window loses
  nothing — and Copy summary puts the verdicts and notes on
  the clipboard for a bug report. The pass records your own
  verdicts; it does not run the checks for you.

### Changed

- **Compact Focus is the default shell layout for new
  installs.** A first run with no saved layout starts in
  Compact Focus. Nothing changes for existing installs: a
  layout already picked — Standard included — is kept exactly
  as saved.

### Removed

- **The token-usage ring is gone.** The circle counter that sat
  in the toolbar beside the zoom controls (added in v1.8.2) is
  removed, along with its breakdown panel and the "Estimated
  context limit" setting. The Muse page never reported a usage
  figure for it to show, so in practice it only ever displayed a
  rough estimate — an estimate dressed as a meter helps nobody.
  The Bookmarks button keeps its place in the same corner.

# What's new

The release notes MuseDesktop shows on the first launch of a new version (S9 #82). This file is generated from CHANGELOG.md's current-version section at release time (see RELEASE_CHECKLIST.md) — do not edit it by hand; a test pins it to the changelog so the two can't drift.

## [1.8.3] — Unreleased

Follow-on slice R7C (directed 2026-10-27): a beep when an
approval card needs attention.

### Added

- **Approval beep.** When Muse shows an approval card — a
  permission prompt waiting for your click — while the
  MuseDesktop window isn't the active one, the app beeps
  once and flashes its taskbar button, so you hear about the
  prompt without watching the window. One beep per card,
  with a short cooldown between cards, and never a beep for
  a card that arrived while you were already looking at the
  window. MuseDesktop only NOTICES the card: the watcher
  is observe-only and can never approve, deny, or dismiss
  anything — the click is always yours. On by default;
  turn it off under Settings → Tray & notifications ("Beep
  when an approval needs my attention"). Detection watches
  for the page's own approval cards as best it can — if
  the page changes its cards, the beep can quietly stop
  (see Known limitations).

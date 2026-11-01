# What's new

The release notes MuseDesktop shows on the first launch of a new version (S9 #82). This file is generated from CHANGELOG.md's current-version section at release time (see RELEASE_CHECKLIST.md) — do not edit it by hand; a test pins it to the changelog so the two can't drift.

## [1.9.0] — Unreleased

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

- **Approval alerts can pop a toast as well as beep.** When
  Muse shows an approval card while the MuseDesktop window
  isn't the active one, the alert can now also appear as a
  Windows toast notification: Settings → Tray & notifications →
  "Toast notification when approval is needed" (on by
  default), beside the beep switch. The two channels are
  independent — beep only, toast only, both, or neither — and
  the toast says only that an approval is waiting, never what
  the card asks. MuseDesktop still only notices the card;
  the approving click is always yours. Toasts are shown
  through the bundled NotifyKit library, and the app keeps a
  Start Menu shortcut named "MuseDesktop" that carries its
  notification identity, which is what Windows needs to
  deliver them.


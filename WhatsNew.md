# What's new

The release notes MuseDesktop shows on the first launch of a new version (S9 #82). This file is generated from CHANGELOG.md's current-version section at release time (see RELEASE_CHECKLIST.md) — do not edit it by hand; a test pins it to the changelog so the two can't drift.

## [1.9.0] — Unreleased

### Added

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

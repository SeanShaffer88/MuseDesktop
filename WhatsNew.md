# What's new

The release notes MuseDesktop shows on the first launch of a new version (S9 #82). This file is generated from CHANGELOG.md's current-version section at release time (see RELEASE_CHECKLIST.md) — do not edit it by hand; a test pins it to the changelog so the two can't drift.

## [1.8.7] — Unreleased

Follow-on slice R7C2 (directed 2026-10-27): the approval beep,
fixed — from SEAN's field test of v1.8.3.

### Fixed

- **The approval beep can hear the card now.** The v1.8.3
  watcher only believed a card when it found an approve button
  and a deny button side by side, both with exact labels, inside
  a small box — and on the real page it never matched, so the
  beep never sounded. The watcher now leads with the rule from
  SEAN's own browser alert script, which works on the real page:
  any visible button whose label starts with "Allow". The old
  pair rule stays on as a back-up signal, the box-size condition
  is gone, and the watcher speaks up sooner — about a third of
  a second after a card appears, not most of a second.
- **The beep repeats while a card waits.** If an approval card
  is still waiting and you still haven't come back to the
  window, the beep sounds again every 20 seconds, so a card
  that appears the moment you step away isn't missed for good.
  Answering the card, or coming back to the window, ends the
  reminders for that card.
- **The log now says what the beep did, and why.** Every
  detection, beep, reminder, and skip (window active, setting
  off, too soon after the last beep) leaves a short line in the
  app log — which rule matched and the reason, never anything
  the card itself said.

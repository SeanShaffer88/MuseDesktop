# What's new

The release notes MuseDesktop shows on the first launch of a new version (S9 #82). This file is generated from CHANGELOG.md's current-version section at release time (see RELEASE_CHECKLIST.md) — do not edit it by hand; a test pins it to the changelog so the two can't drift.

## [2.2.0] — 2026-11-02

### Added

- **Quick ask.** A small Muse window docked at the bottom-right
  corner of the screen, for one quick question without opening
  the main window: press Ctrl+Shift+Space anywhere (or use the
  ⋯ menu, the tray menu, or Ctrl+K) and the card appears, signed
  in as the profile you're using, on a fresh conversation. Type
  in the page's own message box and send as usual — the card is a
  plain page, so drafts and the other conversation tools don't
  run inside it, and nothing is sent for you. "Continue in main
  window" at the card's foot opens the same conversation in the
  main window and closes the card; Esc closes it outright.
  Closing and reopening starts over with a fresh conversation —
  it is a one-off tool, not a second main window. The corner
  card can be turned off (hotkey, menus, and palette together)
  in Settings → Keyboard & accessibility.

- **Pinned conversations.** A conversation you come back to can
  be pinned: open it and choose "Pin this conversation" from the
  ⋯ menu (or Ctrl+K), and it joins a short list — the ⋯ menu's
  "Pinned conversations" submenu and a new Pinned tab in the
  Library window — one click from anywhere. The same menu item
  unpins it again. Pins belong to the profile they were pinned
  under, are ordered most recently pinned first, and keep the
  title the conversation had at pin time, even if the
  conversation is renamed later. A profile holds up to 50 pins.
  Pins are stored on this PC only and are never exported.


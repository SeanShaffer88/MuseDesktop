# What's new

The release notes MuseDesktop shows on the first launch of a new version (S9 #82). This file is generated from CHANGELOG.md's current-version section at release time (see RELEASE_CHECKLIST.md) — do not edit it by hand; a test pins it to the changelog so the two can't drift.

## [2.3.0] — 2026-11-11

### Added

- **Dual mode — two independent chats.** Open it from the ⋯ menu
  or Ctrl+K → Dual mode. On first use the current Muse page opens
  on the left and Home on the right, on the same active profile. Each pane
  has its own page composer, Back/Forward, Reload, New chat, and
  Open in main controls, with a resizable divider. There is no
  shared prompt or automatic send; Compare mode stays separate.
  Downloads use the main window; Dual explains that rather than
  bypassing the download manager's safety rules.

- **Both Dual chats remembered.** Reopening Dual, even after
  restarting MuseDesktop, returns to the last chat on each side.
  Only Muse chat paths are kept per profile on this PC; queries,
  fragments, sign-in callbacks, message text and unsent drafts are
  never saved. A failed load does not replace a remembered chat.

- **Remembered pane widths.** Dual keeps your preferred split per
  profile. Double-click the divider, or focus it and press Home,
  to return to equal widths. Minimum pane sizes still apply.

- **Independent chat zoom.** Each Dual pane has its own minus,
  plus and percentage-reset buttons. Zoom one conversation without
  changing the other; both zoom choices are remembered per profile.

- **Screenshot choices.** "Screenshot to conversation…" is now a
  small menu: the original drag-a-rectangle over any screen, a
  region drag on one named screen, or a whole screen in one
  click. Every choice starts the same new conversation with the
  picture attached and the starter line in the message box, and
  the captured image is still deleted straight after the attach
  attempt, whatever happened.

- **Context packs can include saved pages.** A pack can now hold
  reading-list pages alongside files, notes, and snippets. At
  "Start conversation with this pack", a saved page contributes
  the text of the tab it's open in — open it first if you want
  it included; a page that isn't open, or has left the reading
  list, appears as a one-line note saying so. The same per-item
  and whole-pack length limits apply.

- **Pinned tab search.** The Library's Pinned tab has a search
  box that filters the pins by title (or page address), so even
  a full list stays quick to scan. Clearing the search brings
  the whole list back; unpinning works from a filtered list too.

### Changed

- **Quick ask follows your cursor.** The corner card now docks
  at the bottom-right of the screen your cursor is on instead
  of always the main screen. And if "Continue in main window"
  can't be taken — the main window is still loading, or the app
  is locked — the card now says why at its foot instead of
  silently staying put.

- **Read-aloud gets a floating control bar.** Reading playback
  now runs through a shared playback controller — queue
  position, pause state, and speed decided in one place — and
  the controls move out of the status bar into a small floating
  bar at the bottom-center of the window, shown while a reading
  is playing or paused: pause/resume, stop, a "part N of M"
  position, and speed steps down and up in quarter steps from
  0.5× to 2× (a change applies from the next paragraph, as
  before). Because the bar floats, the controls are now
  reachable in Compact Focus too, where the status bar is
  hidden.

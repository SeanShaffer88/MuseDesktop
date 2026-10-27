# What's new

The release notes MuseDesktop shows on the first launch of a new version (S9 #82). This file is generated from CHANGELOG.md's current-version section at release time (see RELEASE_CHECKLIST.md) — do not edit it by hand; a test pins it to the changelog so the two can't drift.

## [1.7.0] — Unreleased

Round 2 Slice R6 (items 151–160): appearance round 2. The shell
gets its Windows 11 face — backdrop materials, a title bar that can
wear the active profile's color, denser chrome, a mini mode, tray
icon styles, a see-through slider, any accent color you can name,
light/dark on your own schedule, and theme bundles inside the
settings export. As always, only MuseDesktop's own windows are
restyled; the Muse page is never touched, and Windows High
Contrast outranks every choice below.

### Added

- **#151 Backdrop materials.** On Windows 11 the window can sit on
  Mica (a soft tint of your wallpaper) or Acrylic (a stronger
  frosted blur): Settings → Appearance → Window backdrop. Automatic
  means Mica. On Windows 10, with Windows transparency effects off,
  or in High Contrast, the window simply stays solid — no fake
  substitute.
- **#152 Per-profile title bar tint.** The title bar takes the
  active profile's color, so the profile in use is visible at a
  glance. On by default; the switch is in Settings → Appearance.
  High Contrast turns it off.
- **#153 Compact density.** A Compact choice alongside Comfortable
  packs the toolbar and status bar tighter (Settings →
  Appearance → Chrome density). It works with shell text size,
  not instead of it — one sets the spacing, the other the words.
- **#154 Mini mode.** ⋯ menu (or Ctrl+K) → **Mini mode** shrinks
  MuseDesktop to a small always-on-top strip: the profile color,
  the page's icon and title, and the live downloads with **Pause
  all** / **Resume all**. The page itself keeps living in the main
  window, which comes back with **Expand**. Shell chrome only —
  there is no second web view in the strip.
- **#155 Tray icon styles.** The tray icon can be painted Color,
  Monochrome, or tinted with the active profile's color, and its
  attention overlay can be a numbered badge, a plain dot, or off
  entirely: Settings → Appearance → Tray icon / Count overlay.
- **#156 Window opacity.** A slider in the ⋯ menu makes this
  window see-through, from 100% down to 60%. It is per-window and
  lasts for the session only; it turns off in High Contrast, and
  the window goes fully solid whenever the app locks.
- **#157 No white flash.** Every web view the app creates — the
  main window, a second window, the recovery views — now starts on
  the theme's own background color, chosen in one place, so
  nothing flashes white (or black) against the shell before the
  page paints. Under High Contrast it follows the system window
  color instead.
- **#158 Custom accent picker.** In Settings → Appearance, tick
  **Custom accent color** and type any color as six hex digits.
  The color is adjusted until it passes the same measured
  readability (contrast) check as the five presets — on both the
  dark and the light shell — so a custom accent can never be an
  unreadable one.
- **#159 Theme scheduling.** A fourth theme choice, **Follow
  schedule**: light from one time of day, dark from another (the
  times sit right under the theme choice in Settings; the dark
  time may be the earlier one, for night owls). The shell checks
  the clock every minute and switches quietly.
- **#160 Theme bundles.** The appearance face — theme mode,
  accent (preset or custom), and backdrop — travels inside the
  ordinary settings export/import, so one export carries the whole
  look to another PC. The schedule times ride the same export, so
  a Follow schedule look is never stranded without its times.

Core test suite grows by 65 cases (916 → 981); everything above is
**Unverified** on a real machine until the on-machine pass (see
RELEASE_CHECKLIST).

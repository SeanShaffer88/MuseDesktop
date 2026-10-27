# What's new

The release notes MuseDesktop shows on the first launch of a new version (S9 #82). This file is generated from CHANGELOG.md's current-version section at release time (see RELEASE_CHECKLIST.md) — do not edit it by hand; a test pins it to the changelog so the two can't drift.

## [1.8.0] — Unreleased

Round 2 Slice R7 (items 161–170): accessibility & language
round 2. The whole shell — every menu, dialog, Settings page,
and the tray — now speaks through one string catalog, ships in
English and Spanish, and can learn more languages from pack
files. Announcements get per-type switches and a quiet visual
history, every window gets a deliberate keyboard tab order, and
the shell's own text gains line-spacing choices for low-vision
readers. As always, only MuseDesktop's own windows are
translated or restyled; the Muse page is never touched.

### Added

- **#161 Localization framework.** Every string the shell shows
  — menus, dialogs, Settings, the tray, status lines — lives in
  a string catalog instead of being written into the windows.
  A test fails the build if a hard-coded user-facing string
  turns up in the shell, so the catalog can't quietly fall
  behind the windows it feeds.
- **#162 Language picker.** Settings → Language &
  announcements → Shell language: **System default** (follows
  the Windows display language), English, Español, plus any
  language an imported pack adds. No reinstall and no separate
  download. Windows opened after a change use the new language
  right away; windows already open switch at the next launch.
- **#163 Spanish shell language.** A full Spanish translation
  of every shell string, held to the same completeness
  standard as English: the coverage guard below counts it at
  100%, placeholders and all.
- **#164 Translation coverage guard.** If a language lacks a
  string, the shell falls back to English for that string —
  and counts it. Diagnostics shows a per-language coverage
  report, so a partial translation is always visible, never a
  silent gap.
- **#165 Language packs as files.** Anyone can translate the
  shell: export a template holding every string, translate it,
  and import the finished file in the same Settings section.
  The app validates a pack before using it — every string
  present, no unknown strings, placeholders intact — and
  refuses a bad pack with the reason in plain words. Packs
  live in the `language-packs` folder in the data folder.
- **#166 Right-to-left readiness.** The catalog and the shell
  layout know each language's reading direction, and windows
  follow it. No right-to-left language ships yet; the machinery
  is proven in tests with a synthetic Arabic pack, so adding
  one later is translation only.
- **#167 Accessibility audit, extended.** The automated
  audit that checks every control has an accessible name now
  covers every window added since round 1 — the command
  palette, workspaces, profiles extras, the downloads extras,
  bookmarks and the left rail, the mini window, and the R5/R6
  surfaces — and runs as tests on every build.
- **#168 Logical tab order everywhere.** Every window has a
  deliberate keyboard focus order that follows the order a
  person reads the screen, pinned by the same audit so a new
  control can't silently land in the wrong place.
- **#169 Announcement center.** The spoken announcements
  (a download finishing, an update waiting, a timer ending,
  focus mode turning on) get per-type switches in Settings →
  Language & announcements — turn off the kinds you don't
  want spoken and keep the rest. Diagnostics keeps a quiet
  visual history of the last 100 announcements, including the
  ones a switched-off type didn't speak, so nothing the app
  said is ever lost.
- **#170 Shell text spacing.** Line spacing for the app's own
  windows — Standard, Relaxed, or Wide — in Settings →
  Language & announcements, for low-vision readers. (WPF has
  no letter-spacing setting, so the control is line spacing
  only; see Known limitations.)

Core test suite grows by 94 cases (981 → 1075); everything above is
**Unverified** on a real machine until the on-machine pass (see
RELEASE_CHECKLIST).

### Fixed

- **Settings would not open (crash, found in the v1.7.0 field
  pass).** The profile picker in Settings carries a custom item
  template (color dot + name), and an older line of code also set
  its DisplayMemberPath — Windows' UI framework refuses the
  combination and throws, closing the window before it appears.
  The redundant line is gone (the template already shows the
  name), and a new test fails the build if any templated control
  ever gets a DisplayMemberPath again.

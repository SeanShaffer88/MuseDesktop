# What's new

The release notes MuseDesktop shows on the first launch of a new version (S9 #82). This file is generated from CHANGELOG.md's current-version section at release time (see RELEASE_CHECKLIST.md) — do not edit it by hand; a test pins it to the changelog so the two can't drift.

## [1.8.8] — Unreleased

Two changes ship together in v1.8.8 (grouped release):
hotfix R7E2 (directed 2026-10-27) — the Settings topics
rail, made readable, from the field test of v1.8.6 — and
slice R7F: the browser password hardening.

### Fixed

- **The Settings topics list reads clearly again.** The
  topic names and icons down the left side of Settings were
  painted black on the near-black rail in the dark theme —
  a built-in Windows list default quietly overrode the
  window's text color for that one list, so the topics you
  hadn't picked were almost invisible. The rail now uses
  the theme's text color like the rest of the window:
  white in the dark theme, dark in the light theme, in
  every state — picked, hovered, or waiting.

### Security

- **Passwords are never saved now.** The web view's
  password saving is off for good — not a setting, a rule.
  The old "let Muse save passwords" switch is gone from
  Settings, and a saved "on" from an older version is not
  honored.
- **Passwords older versions saved are removed.**
  Upgrading deletes the saved-password store (the files
  named `Login Data` and `Login Data-journal`) from every
  profile's browser folder — the installer does it during
  the upgrade, and the app repeats the same wipe at every
  start, which also covers the portable zip. Your
  sign-ins are cookies, and cookies are never touched:
  you stay signed in to Muse. A site whose password was
  saved will simply ask you to type it again.
- **Form autofill starts off.** On a fresh install the
  web view no longer remembers and refills ordinary form
  entries until you turn it on in Settings → Privacy &
  security. If you already had it on, your choice is kept.
- **Settings → Privacy & security now says what is stored
  where:** no passwords, ever; sign-in cookies in each
  profile's browser folder on this PC, removable with the
  clear-data control; and nothing sent anywhere by the
  app itself.

# Changelog

## [0.3.0] — 2026-10-23

### Added

- **Account profiles.** Run more than one Muse sign-in from one install — e.g. personal and work. Settings → *Account profiles*: add a profile, switch to it (the app restarts with that profile's own WebView2 data folder; the default profile keeps the folder the app has always used, so existing sign-ins are untouched). The ⋯ menu and tray tooltip show the active profile when there's more than one. Profiles can be removed (their local sign-in data is deleted on OK — except the profile the app is currently running, whose folder is in use; see Known limitations). Up to 8 profiles.
- **Link context-menu actions.** Right-clicking a link in a chat now also offers **Open link in browser** (the link policy's external route, without leaving the page first) and **Copy link address**, added to WebView2's own menu.
- **Installer download.** Releases now include `MuseDesktop-Setup-vX.Y.Z.exe` (NSIS, per-user install — no admin): installs to `%LOCALAPPDATA%\Programs\MuseDesktop`, adds a Start Menu shortcut (optional desktop shortcut), and registers an uninstaller in Apps & Features. Uninstalling removes the program but **keeps your settings and sign-in data** unless you answer Yes to the delete-data prompt. The portable zip remains available and unchanged.

### Changed

- **Smaller download:** only English satellite assemblies ship now (the shell UI is English-only; the self-contained runtime otherwise carries a folder per culture).

### Notes

- Profile model and invariants live in `MuseDesktop.Core` (`Profiles.EnsureValid`, called on every settings load) with unit tests; suite now 82 cases. All v0.3.0 behaviors — profile switching, the context-menu items, and the installer end-to-end — are CI/local-build verified but **Unverified** on a real Windows machine until the `RELEASE_CHECKLIST.md` pass runs (new checklist items included).

## [0.2.0] — 2026-10-23

### Added

- **Settings window** (⋯ menu → *Settings…*): grouped, explained controls replace the scattered menu toggles — Start with Windows, close-to-tray, default zoom, downloads folder, ask-where-to-save, update checks, tray memory saving, and clear-data. OK applies; Cancel discards (the window edits a copy).
- **Update notifications.** Once per launch (and on demand from the ⋯ menu) the app checks GitHub Releases; when a newer stable version exists, the status bar says so and the menu item becomes *⬆ Update available*. The check is silent on failure — no dialogs, no nagging — and can be turned off in Settings. This matters because runtime patches now arrive via app releases (the download is self-contained).
- **Download controls.** Downloads can now be **cancelled** from the status bar (✕), a finished download's status line can be **clicked to open the file**, and Settings adds a **downloads folder** choice plus an **ask-where-to-save** (Save-As) option.
- **Clear browsing data / sign out** in Settings: wipes MuseDesktop's own cookies, cache, and site data via the WebView2 profile API and returns to Muse's home page. Chats live in your Muse account and are unaffected.
- **Tray memory saving.** After 5 minutes hidden in the tray, the web view is suspended (`TrySuspendAsync`) and resumed when you return — a resident app shouldn't hold a full browser renderer at idle. It never suspends mid-download, and the page can decline suspension (e.g. during a call), in which case the app simply stays awake.

### Changed

- The ⋯ menu's *Close window to tray* and *Start with Windows* toggles moved into the Settings window (single source of truth); the menu is now Settings…, Check for updates, Open log folder, About.

### Notes

- Update comparison logic and the new settings live in `MuseDesktop.Core` with unit tests (13 new cases; suite now 78). The new runtime behaviors (update notice, suspend/resume, clear-data, Save-As) are CI-built and smoke-tested but join the on-machine checklist in `RELEASE_CHECKLIST.md` — they are **Unverified** on a real Windows machine until that pass runs.

## [0.1.3] — 2026-10-23

### Changed

- **Closing the window now minimizes to the tray by default.** Set by SEAN after v0.1.2's on-machine verification — a Muse client is a resident app, and the tray is its home. The **⋯** menu's *Close window to tray* toggle still turns it off. Note for existing installs: the setting is stored per user, so an install that already saved a preference keeps it — the new default applies to fresh installs (and the toggle is one click either way).

### Added

- README screenshot of the main window from a real v0.1.2 run (conversation content blurred for publication).

### Verified

- **v0.1.2 confirmed working on a real Windows machine (2026-10-23):** clean startups in the log (`WebView2 155.0.4283.62 ready`, no errors), sign-in and voice confirmed by the user. This closes the runtime-verification gate the 0.1.x line had been carrying since the v0.1.0 startup crash.

## [0.1.2] — 2026-10-21

### Changed

- **Retargeted .NET 8 → .NET 10 LTS.** .NET 8 reaches end of support on November 10, 2026; .NET 10 is supported until November 2028. A `global.json` now pins the SDK band so local and CI builds use the same toolchain.
- **The release download is now self-contained.** The .NET 10 runtime is bundled into the zip, so a fresh PC needs nothing but the WebView2 Runtime (which the app already detects and explains). This trades a much larger download (tens of MB instead of 0.7 MB) for zero runtime prerequisites — the right trade for a public download.
- CI: GitHub Actions moved to their Node 24 majors (checkout v5, setup-dotnet v5, upload-artifact v6); test packages updated (Microsoft.NET.Test.Sdk 18.11.0, xunit 2.9.3).

### Security

- DevTools is now enabled in Debug builds only. Release builds no longer expose a scripting surface into the live, signed-in Muse session.

### Added

- The `--smoke-test` mode now also shows the window and requires WebView2 to actually initialize (45 s budget), not just construct — and `RELEASE_CHECKLIST.md` documents the on-machine release gate (sign-in persistence, tray, downloads, voice, placement restore, screenshots).

## [0.1.1] — 2026-10-21

### Fixed

- **Startup crash on first launch (v0.1.0 field report).** Creating the main window threw `XamlParseException` (`Baml2006.TypeConverterMarkupExtension`): the XAML `Icon="muse.ico"` attribute needed the icon as a WPF pack resource, but the file was only embedded as the exe's Win32 icon. The icon is now included as a WPF `Resource` and is assigned in code with a fallback to the exe icon, so an icon problem can never take startup down again.
- Tray creation failures no longer prevent the window from starting.
- Unhandled-exception logging now records the full exception (inner exceptions and stack) instead of only the outer message, which is what hid this crash's cause.

### Added

- `--smoke-test` startup mode that constructs the real main window and exits 0/1, run as a CI step against the published exe — v0.1.0 passed every compile and unit test yet died at window construction, so CI now exercises construction itself.

## [0.1.0] — 2026-10-20

### Added

- Native Windows client for muse.ai: WPF shell over the Microsoft WebView2 control (SDK 1.0.4258.31), .NET 8, Windows 10/11 x64.
- Persistent WebView2 profile (`%LOCALAPPDATA%\MuseDesktop\WebView2`) so the Muse sign-in survives restarts; the last Muse page is restored on launch.
- Black/red product-family toolbar and status bar with Back / Forward / Reload / Home, page title + host display, zoom controls with % readout, Open-in-browser, and a ⋯ menu (Close to tray, Start with Windows, Open log folder, About).
- System tray icon with Open / Reload / Exit; single-instance behavior — a second launch wakes the running window.
- Optional Start with Windows (`--background` tray launch via HKCU Run).
- Link policy: HTTPS-only top-level navigation; new-window requests stay in-app only for Muse and known sign-in hosts, open in the default browser otherwise, and dangerous schemes are refused. Host matching is exact/subdomain so look-alike domains never qualify.
- Permission policy: notifications and microphone auto-granted for `https://muse.ai` only; all other permission requests denied and logged.
- Downloads reported in the status bar with progress % and ETA; files land in the default Downloads folder.
- Plain-language failure states: offline/load-failure overlay with Retry, missing WebView2 Runtime overlay linking Microsoft's official Evergreen bootstrapper, and a crash overlay for WebView2 process failures.
- Local diagnostics log (`%APPDATA%\MuseDesktop\logs`, 1 MB rollover, URLs logged host-and-path only) and atomic, corruption-tolerant JSON settings.
- `MuseDesktop.Core` library with the URL policy, settings store, zoom math, paths and log — covered by unit tests that run on any platform.

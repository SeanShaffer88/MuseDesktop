# Changelog

All notable changes to MuseDesktop. Format follows Keep a Changelog; versions use semantic versioning.

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

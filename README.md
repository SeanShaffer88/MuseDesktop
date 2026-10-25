# MuseDesktop

A native Windows desktop client for [Muse](https://muse.ai) — Meta's personal AI agent.

Muse ships apps for iOS, Android and macOS, and a web app at muse.ai — but no Windows app. MuseDesktop fills that gap: a small, focused Windows program that keeps Muse one click away, signed in, with the desktop behaviors a browser tab can't give you.

> MuseDesktop is an independent open-source project. It is not made by, endorsed by, or affiliated with Meta. "Muse" is a product of Meta; this app simply displays muse.ai in a native window.

## TL;DR

Muse has no Windows app, so this is one: a small native program that opens muse.ai in its own window, keeps you signed in, and lives in the system tray. It adds the desktop touches a browser tab can't — downloads with progress, cancel, and a history window, multiple sign-in profiles, a settings window, update notices, and sensible link handling. Free, open source (MIT), no telemetry; you only need Windows 10/11 (x64 or ARM64) and the WebView2 Runtime (usually already installed). Download the installer from Releases and run it.

## What you get

- **A real app, not a tab** — its own window, taskbar icon, and Alt-Tab entry; no browser chrome competing for space.
- **Stay signed in** — a dedicated WebView2 profile on your PC keeps your Muse session between launches, and the app reopens the last Muse page you were on.
- **Tray support** — the tray menu runs the whole app while it's hidden: Open, **Quick dictate**, New window, Reload, **Downloads** (the manager, plus your newest finished files), **Settings…**, **Switch profile**, a web-view status line, and Exit. The tray icon wears your active profile's accent color as a small dot, and shows an unread-count badge on the icon and taskbar button **only** when Muse's page title itself carries one (like "(2) Muse") — it never guesses. Closing the window minimizes to the tray by default; after a while hidden (1 / 5 / 15 minutes, or never — your choice in Settings) the web view suspends to free memory until you come back, and suspend always waits out downloads and stays out of the way after the microphone has been used, so a voice session is never slept under. Single-click instead of double-click to reopen is a Settings option, and right-clicking the **taskbar** button offers a Jump List: Open Muse, Settings, Quick dictate, and your most recent profiles. "Start with Windows" and "start in the tray" are separate toggles — a sign-in launch can open the window instead, and double-clicking the app always opens it.
- **Downloads with progress and control** — files Muse makes for you download in parallel with live progress %, an estimated time remaining, and a transfer speed in the status bar; the taskbar button mirrors overall progress. The ⋯ menu's **Downloads…** window is a small manager: every active download with its own pause/resume/cancel (plus retry for a download that failed in this session), and a searchable history — filter completed/failed, sort by date, size, or name — with Open / Show in folder / Copy path / Open with… / Delete file. Choose the folder (shared or per profile), ask-where-to-save, what happens when a file name collides, how much history to keep (50/200/1000), and whether a tray balloon tells you a download finished while you were away, all in Settings.
- **Settings window** — one place (⋯ menu → *Settings…*, or Ctrl+,) for startup behavior, close-to-tray, default zoom, downloads, update checks, tray memory saving, and **export/import** of your settings. It has a **search box** (type "portable" or "quiet" and the right section lights up), reopens at the section and size you left it, and offers a forgiving **Reset all settings** — your current settings are exported to a backup file first, and profiles/sign-ins are never touched by a reset. A one-screen **setup wizard** greets a fresh install (and can be re-run from Settings → Behavior). Each profile can reopen its last page, start at the Muse home page, or start at a specific Muse page you choose.
- **Privacy & security** (Settings section) — clear exactly what you mean to: **cookies only, cache only, or all site data**, for one profile or all of them, with the consequence stated before anything is deleted. See each profile's **saved permissions** and change them (allow / block / forget), each profile's **storage use and cookie count** (counts only — cookie values are never displayed), and a counts-only **privacy report** of what the app has blocked. An optional **app lock** (a password or PIN you set in the app) can be required at startup and when the app hides to the tray — it guards the open app, not the data on disk, and the app says so. Settings **export can be password-encrypted**; the ⋯ menu's **Connection security…** shows the current site's connection and runtime version, and certificate problems get a plain-language explanation with the safe choice first; **View log…** opens the app's log with a built-in redaction check before you share it.
- **Make it yours** — shell **theme** (Dark, Light, or Follow Windows), five contrast-checked **accent colors**, **toolbar buttons** you can show/hide and reorder, and a **shell text size** (100/125/150%) separate from page zoom. All of it styles MuseDesktop's own windows only — the Muse page is never restyled.
- **Portable mode** — drop an empty `portable.txt` beside `MuseDesktop.exe` in the zip build and settings, download history, and logs live in a `data` folder beside the exe instead of `%APPDATA%` — carry it on a USB stick. (Sign-in data stays in your user profile for now; see *Where things live*.)
- **Updates, in-app** — the app checks GitHub Releases and tells you in the status bar when a newer version is out. Since v0.13.0 the update window **downloads the installer for you** (with progress), **verifies its SHA-256 fingerprint against the release's published sums** before it can run, and installs only when you say so — never silently. You can snooze reminders (tomorrow / next week), choose how often checks run, opt into **pre-releases** (Stable channel is the default and never sees one), and see a **What's New** window once after each update. If an update misbehaves, Diagnostics can **reinstall the previous version** — when that version itself arrived through the in-app updater (the honest limit is in KNOWN_LIMITATIONS). Checks can be turned off in Settings → Behavior.
- **Multiple accounts** — profiles (Settings → *Account profiles*) keep separate Muse sign-ins on one PC, each with its own session data — up to 32 of them. Rename them, give each an accent color (shown as a dot in menus, the tray, and Settings), and switch from the ⋯ menu, the tray menu, or Settings (the app restarts into the chosen profile). Each profile can have its own zoom and downloads folder, Settings shows how much disk each one uses and when it was last used, and a new profile can copy an existing one's settings (never its sign-in). **Create desktop shortcut** in Settings puts a per-profile shortcut on the Desktop; from the command line, `MuseDesktop.exe --profile <id>` launches straight into a profile.
- **Sensible link handling** — Muse and sign-in pages stay in the app; ordinary web links from chats open in your default browser; dangerous schemes (`file:`, `javascript:`, …) are refused. Right-clicking a link adds *Open link in browser* and *Copy link address*.
- **Voice and alerts** — Muse origins get microphone (voice dictation) and notification permission; every other permission request is denied rather than silently granted. MuseDesktop's own balloons and the optional taskbar attention flash respect Windows Do Not Disturb and an in-app **quiet hours** schedule you set — and when something is kept quiet, the log says so.
- **Keyboard-friendly** — Back/Forward/Reload/Home in the toolbar, zoom controls with a % readout (Ctrl + / − / 0), F11 full screen, and standard browser accelerators inside the page.
- **Find, print, and focus (v0.5.0)** — Ctrl+F finds text in the page with a match count; Ctrl+P prints and *Save as PDF…* exports the page; F8 focus mode hides the toolbar and status bar; a 📌 pin keeps the window on top; *Open in new window* shows the current page in a lightweight second window on the same sign-in; right-click or long-press Back/Forward jumps through this session's history, and Ctrl+Shift+T reopens the last page you left. Each profile remembers its own window placement, and load failures now explain themselves in plain words (offline, DNS, certificate…) and retry once when your connection comes back.
- **Help that stays in the app (v0.14.0)** — ⋯ → *Help & troubleshooting…* opens offline pages for the common problems (sign-in loops, voice not hearing you, silent notifications, a missing WebView2 Runtime, failing downloads, a forgotten app password, pages that won't load), each with a button that takes you to the fix. **Diagnostics** now shows everything in one copyable report — versions (app, Windows, .NET, WebView2), profile, update channel, data sizes, recent errors — and can export a **support bundle**: one .zip (diagnostics, redacted log tail, sharing-sanitized settings, download history) previewed file-by-file before it's written and uploaded nowhere. Settings → Privacy & security has guided **microphone and notification tests**, and **About** is a full window with the update check and the app's licenses/notices.
- **Recovers instead of dying (v0.12.0)** — if the page's engine crashes, MuseDesktop reloads your page automatically once (and rebuilds the whole web view if the engine itself went down); if your connection drops or the PC sleeps mid-load, the failed page retries once when things come back — one shared recovery budget, never a reload loop. A page that stops answering gets a polite banner offering Reload or Wait — never an automatic reload that could cut across a voice call. **Safe mode** (`--safe-mode`, or hold Shift at launch) starts at the home page with no update check when something's off, and Settings → Data has a **guided reset** (backup first, then your choice of steps), **rotating backups** of settings and download history (five kept, restore in Settings), an optional **per-profile data cap** that only ever empties disposable caches (cookies and sign-ins are untouchable), and a **Diagnostics** window (⋯ menu) with the web runtime's health, your Windows proxy setting, current memory, and how fast the last launches started.
- **Summon it from anywhere (v1.0.0)** — a **global hotkey** (Ctrl+Alt+M, changeable in Settings → Accessibility & keyboard) shows or hides MuseDesktop from any app; if another program owns the combination, Settings says so instead of the key silently failing. **Quick dictate** (tray menu or Jump List) opens the window with the Muse page focused, one step from Muse's voice button — and if the microphone isn't granted yet, it says so rather than faking a dictation start. **`musedesktop://` links** open the app at fixed places (`musedesktop://settings`, `…/downloads`, `…/profile/<id>`…) — a link can never carry a web address, and while the app is locked, links and command-line flags wait behind the lock. Dragging a file over the window lights the frame and points you at the page, where Muse's own upload handling takes it (dropping on the page itself has always worked). The full flag and link list is under *Command line & links* below.
- **Private by construction** — no telemetry, no accounts of its own, no data collection. The only files it writes are its settings, a rolling local log, and the WebView2 profile (cookies/local storage) — all under your user profile, all deletable by you at any time.
- **Forgiving** — second launches wake the running window instead of opening a duplicate; a corrupted settings file is preserved as a backup and defaults are used; load failures explain themselves and offer a retry.

## Screenshots

The main window, signed in to Muse — from a real run of v0.3.1 on Windows (conversation content blurred):

![MuseDesktop main window](docs/screenshots/main-window.png)

The Settings window, from the same machine — behavior, account profiles, zoom, downloads, and data controls:

![MuseDesktop Settings window](docs/screenshots/settings.png)

Still to capture from a real run:

1. The **⋯** (More options) menu open, showing Settings… / Check for updates.
2. A file downloading from Muse, showing the status-bar progress % and ETA (and the ✕ cancel button).
3. The tray icon's right-click menu.

## Requirements

- Windows 10 or Windows 11 (x64, or ARM64 — native builds ship for both)
- The Microsoft Edge **WebView2 Runtime** — preinstalled on Windows 11 and most Windows 10 PCs. If it's missing, MuseDesktop tells you and links you to Microsoft's installer.
- Nothing else: the download is **self-contained** — the .NET 10 runtime is bundled in, so there is no separate .NET install.

## Install

**Installer (recommended):** download `MuseDesktop-Setup-vX.Y.Z-x64.exe` (or `-arm64.exe` on a Windows-on-ARM PC) from the [Releases](../../releases) page and run it. It's a per-user install (no admin needed): program in `%LOCALAPPDATA%\Programs\MuseDesktop` (you can pick a different folder — upgrades reuse it), a Start Menu shortcut, an optional desktop shortcut (off by default; your choice is remembered for upgrades), registration of `musedesktop://` links (see *Command line & links*), and an entry in Apps & Features for uninstalling. Uninstalling keeps your settings and sign-in data unless you choose to delete them when asked. A winget manifest is prepared in `packaging/winget/` but **not yet submitted** — `winget install MuseDesktop` is not live yet.

**Portable zip:** download `MuseDesktop-vX.Y.Z-windows-x64.zip` (or `-windows-arm64.zip`), unzip anywhere, and run `MuseDesktop.exe`.

Either way: the app is not code-signed yet, so Windows SmartScreen may warn on first run — choose **More info → Run anyway**. Your data folders (`%APPDATA%\MuseDesktop` and `%LOCALAPPDATA%\MuseDesktop`) hold settings and sign-in profiles; deleting them signs you out.

## Where things live

| What | Where |
| --- | --- |
| Settings | `%APPDATA%\MuseDesktop\settings.json` |
| Download history | `%APPDATA%\MuseDesktop\downloads.json` (names, sizes, dates, paths, and completed/failed state of downloads — never their source URLs) |
| Diagnostic log | `%APPDATA%\MuseDesktop\logs\musedesktop.log` (1 MB, single rollover; URLs are logged host-and-path only, never with query strings) |
| Web profile (login session) | `%LOCALAPPDATA%\MuseDesktop\WebView2` for the default profile; extra profiles get sibling folders (`WebView2-<id>`) |
| Downloads | Your Downloads folder, or the folder chosen in Settings |
| Settings backups | `settings-backup-<date>-<time>.json` in the settings folder — written automatically before a *Reset all settings*, restorable via Settings → Import |
| Rotating backups | `backups\auto-settings-*.json` and `backups\auto-downloads-*.json` in the settings folder — the five most recent of each (v0.12.0), restorable from Settings → Data |
| Startup timings | `startup-timings.json` in the settings folder — the last ten launches' start → window → web view → first page timings (v0.12.0), shown in ⋯ → Diagnostics |
| Update installers | `updates\` in the settings folder — the installer the in-app updater downloaded (`current\`) and the one rollback copy (`previous\`) (v0.13.0); safe to delete, the rollback offer just goes away |
| Pending launch intent | `pending-intent.json` in the settings folder — a flag or `musedesktop://` link handed from a second launch to the running app (v1.0.0); written and deleted in the same moment, holds no web addresses |

In **portable mode** (an empty `portable.txt` beside the exe), Settings, Download history, and the Diagnostic log live under a `data` folder beside the exe instead of `%APPDATA%\MuseDesktop`. The Web profile (login session) stays in `%LOCALAPPDATA%` in this version — a portable copy does not carry your sign-in. Nothing is moved automatically in either direction.

## Keyboard shortcuts

| Keys | Action |
| --- | --- |
| Alt+Left / Alt+Right | Back / Forward |
| Right-click / long-press Back or Forward | Jump through this session's page history |
| Ctrl+Shift+T | Restore the last closed page |
| Ctrl+F | Find in page (Enter next, Shift+Enter previous, Esc closes) |
| Ctrl+P | Print (⋯ menu also has Save as PDF…) |
| F8 | Focus mode — hide the toolbar and status bar (Esc exits) |
| Ctrl+R or F5 | Reload |
| Ctrl+Plus / Ctrl+Minus | Zoom in / out (finer steps near 100%; Settings → Zoom can extend the range to 25%–500%) |
| Ctrl+0 | Reset zoom to 100% |
| Ctrl+, | Open Settings |
| Ctrl+Shift+D | Open Downloads |
| Alt+Home | Go to Muse home |
| Ctrl+/ | Keyboard shortcuts — a searchable list of every shortcut |
| F11 | Full screen (Esc exits) — works from the window chrome and the page's own full-screen requests are honored |
| Ctrl+Alt+M | **Global** show/hide — works from any app: brings MuseDesktop forward, or hides it to the tray (v1.0.0; changeable, see below) |

The Settings, Downloads, and Home keys can be changed in **Settings → Keyboard & accessibility** (click a key, press the new combination). That section also holds the **Reduce motion** setting (Follow Windows / On / Off — it quiets the taskbar attention flash) and the **global hotkey**: registered with Windows itself, so it works while any app has focus — press it to show MuseDesktop, press it again to hide it. It can be changed or turned off in the same section; it must include Ctrl or Alt, it can't share a combination with an in-app shortcut, and if another app already owns the combination, the section says so plainly. While the app is locked, the hotkey only ever shows the lock screen. Version 0.10.0 also adds a shell accessibility pass — visible focus everywhere, Windows High Contrast support, screen-reader announcements for downloads and updates, and focus that returns to where you were after dialogs close.

## Command line & links

`MuseDesktop.exe` accepts flags, and Windows accepts `musedesktop://` links — one surface that desktop shortcuts, the taskbar Jump List, and your own scripts/automation all use. If MuseDesktop is already running, a second launch doesn't open a second copy: the flags arrive as actions in the running app (a running app receiving `--settings` opens its Settings; `--profile` asks before switching, exactly like switching inside the app). While the app is locked, flags and links wait behind the lock and the newest one applies right after you unlock. Unknown flags are logged and ignored.

| Flag | What it does |
| --- | --- |
| `--profile <id>` | Start in (or switch to) the profile with this id — the ids are the lowercase names in Settings → Account profiles |
| `--background` | Start hidden in the tray (how "Start with Windows" launches it) |
| `--home` | Open at the Muse home page |
| `--settings` | Open the Settings window |
| `--downloads` | Open the Downloads window |
| `--dictate` | Quick dictate: show the window with the Muse page focused, ready for Muse's voice button |
| `--safe-mode` | Start at the home page with no automatic update check, keeping your settings — for when something's off (the app lock still applies) |
| `--help` | Show the command-line usage in a dialog |
| `--version` | Show the app version in a dialog |

`--background` and `--safe-mode` describe how the app *starts*, so on an already-running app they're noted in the log and otherwise ignored.

Links work the same way (the installer registers the scheme for your user account; portable installs register it from the toggle in Settings → Behavior, which also reports if another app owns the scheme — MuseDesktop never overwrites someone else's registration):

| Link | Opens |
| --- | --- |
| `musedesktop://open` | The main window |
| `musedesktop://home` | The window at the Muse home page |
| `musedesktop://settings` | The Settings window |
| `musedesktop://downloads` | The Downloads window |
| `musedesktop://help` | Help & troubleshooting |
| `musedesktop://profile/<id>` | The window in that profile (an unknown id just opens the window and logs why) |

That's the complete list, on purpose: a link can never carry a web address (`musedesktop://open?url=…` is refused and logged, host-and-path only) or open anything else.

## Building from source

Requires the .NET 10 SDK (see `global.json`).

```powershell
dotnet restore MuseDesktop.sln
dotnet build MuseDesktop.sln -c Release
dotnet test tests\MuseDesktop.Core.Tests -c Release
dotnet publish src\MuseDesktop\MuseDesktop.csproj -c Release -r win-x64 --self-contained true
```

Architecture: `MuseDesktop.Core` holds all testable logic (URL policy, settings, profiles, zoom, update checks, paths, logging) as plain .NET 10 with no Windows dependencies; the WPF app in `src\MuseDesktop` is a thin shell over Microsoft's WebView2 control. See [DECISIONS.md](DECISIONS.md) and [KNOWN_LIMITATIONS.md](KNOWN_LIMITATIONS.md).

## Contributing

Issues and pull requests are welcome. Keep changes small and focused, put testable logic in `MuseDesktop.Core` with tests, and run the project gate before opening a PR: `dotnet build MuseDesktop.sln -c Release` with zero errors and the full core test suite passing (see [AGENTS.md](AGENTS.md)). By contributing, you agree your work ships under the project's MIT license.

## License

MIT — see [LICENSE](LICENSE).

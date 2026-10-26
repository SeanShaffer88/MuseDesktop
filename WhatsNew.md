# What's new

The release notes MuseDesktop shows on the first launch of a new version (S9 #82). This file is generated from CHANGELOG.md's current-version section at release time (see RELEASE_CHECKLIST.md) — do not edit it by hand; a test pins it to the changelog so the two can't drift.

## [1.2.0] — Unreleased

Round 2, Slice R2 (items 111–120) of the enhancement program: workspaces & sessions.

### Added

- **Workspaces (#111)** — save the windows you have open as a named workspace: ⋯ menu → *Save current arrangement as workspace* — one click, no prompt (rename it later in Settings). A workspace remembers each window's profile, where it sits, and which Muse section it shows, stored as an app route ("/chat", "/projects") — never the full page address, so no query string or token from an address can ever be written into a saved layout.
- **Apply workspace (#112)** — from the ⋯ menu, the tray, Ctrl+K, or Settings: windows open and close to match the layout, and the main window navigates (it never closes). Two honest asks: if the workspace belongs to another profile, MuseDesktop restarts into that profile first (with your OK) and applies it there; and if applying would close a window while a download is running, it asks first — the download itself belongs to the app, not the window, and keeps going either way.
- **Startup workspace (#113)** — Settings → *Workspaces & sessions* can mark one workspace to open at launch: its windows open instead of the single default window.
- **Session snapshot (#114)** — ⋯ menu → *Take session snapshot* remembers the open windows once; *Restore session snapshot* brings them back later, through the same planner (and the same download guard) as a workspace apply. The snapshot lives in its own file, separate from the app's own session bookkeeping, so neither overwrites the other.
- **Switching surfaces + accent override (#115)** — workspaces apply from the tray's new *Workspaces* submenu, the ⋯ menu, and the command palette: the provider seam from R1 lights up with one "Apply workspace:" command per workspace. A workspace can also carry an accent override — while it's the active workspace, the shell accent uses its color. Your saved Appearance accent is never rewritten; the override is session-only and lifts when another workspace (or none) is active.
- **Portable workspaces (#116)** — workspaces live in your settings, so Export/Import carries them, including the password-encrypted export; an older settings file without them simply loads with none. They version with the settings file's schema, and a damaged entry is repaired on load the way profiles are: windows naming a deleted profile drop, and a route that can't be one reads as Home.
- **Crash-session restore (#117)** — the app keeps a small journal of the open windows (routes only). If MuseDesktop didn't shut down cleanly, the next launch offers *Restore previous windows* — once. If a restored session dies before any page loads, the offer is not repeated: a restore that seems to be the problem is never looped.
- **Auto-save session (#118)** — Settings → *Workspaces & sessions* → "Pick up where I left off" (off by default): after a normal exit, the windows you left open come back at the next launch. Routes only, like everything in this slice.
- **Workspace glyphs (#119)** — each workspace can wear one of twelve text glyphs (★ ● ◆ ▲ ■ ♥ ♪ ☀ ☾ ⚑ ✚ ⌂), shown beside its name in the tray menu, the ⋯ menu, and the palette — the same fixed-palette discipline as profile colors, so a glyph renders the same everywhere instead of depending on an icon font.
- **Workspace management (#120)** — Settings → *Workspaces & sessions*: rename, duplicate ("Focus" → "Focus copy"), delete, glyph, accent override, the startup choice, and **Apply now**. Deleting follows the profiles' deferred-safety pattern: it edits the Settings copy only, so Cancel undoes it and OK makes it stick — and deleting a layout never closes a window or touches any data.

### Notes

- Core test suite grows by 42 cases (675 → 717). All v1.2.0 runtime behavior is **Unverified** on a real Windows machine pending the `RELEASE_CHECKLIST.md` R2 pass.

## [1.1.0] — Unreleased

Round 2, Slice R1 (items 101–110) of the enhancement program: the command palette and the window command surface.

### Added

- **The command palette (#101)** — Ctrl+K opens a keyboard-first search over everything the shell can do: every action, your profiles, every Settings section, the Help pages, your recent downloads, and your open windows. A few letters are enough — they don't have to be the start of the words — and the actions you run are remembered (action names only, never page or file names) and pinned as "Recent" the next time you open it.
- **Palette providers (#102)** — the palette is fed by one registry of providers: built-in actions, profiles, Settings sections, Help topics, recent downloads (Open and Show in folder), and open windows each contribute their commands. Workspaces get their provider seam now and light up in a later round-2 slice.
- **The window switcher (#103)** — ⋯ menu → *Switch window…* (or the palette's own "Switch window…" command) lists your open windows most-recently-used first, each with its profile color dot and page title; the window you were just in is already selected — arrow keys and Enter jump straight there.
- **Window actions in the palette (#104)** — for every open window: tile left, tile right (to half of the monitor's work area, so the taskbar stays visible), move to the next monitor, toggle always-on-top, and — for the main window — toggle focus mode.
- **Go-to-profile chords (#105)** — Ctrl+Shift+1 through Ctrl+Shift+9 switch to the 1st–9th profile, in profile order. In-app only, on purpose: there is no Windows-wide version, because Ctrl+Shift+number belongs to other apps too and MuseDesktop won't take those keys away from them everywhere.
- **Settings search, upgraded (#108)** — Settings search now uses the palette's one fuzzy matcher and ranks the best match first (a word in a section's own title counts for more than the same word in its keyword list); the Settings window scrolls to the best match instead of the first in layout order.
- **A Commands help page (#109)** — Help gains an eighth page, *Commands & the command palette*: what the palette is, the switcher, the chords, and the tray middle-click — ending with the full command list, generated from the palette itself every time the page is shown, so it can never drift from what Ctrl+K really offers.
- **Tray middle-click (#110)** — middle-clicking the tray icon opens the palette in its compact form. While the app is locked it answers with the lock screen, like every other tray surface.

### Changed

- **Always-on-top has one setter now** — the ⋯ menu check, the toolbar pin, and the palette's window action all funnel through the same code path, so they can never disagree about the setting, the check mark, or the status line.

### Notes

- Core test suite grows by 31 cases (644 → 675). All v1.1.0 runtime behavior is **Unverified** on a real Windows machine pending the `RELEASE_CHECKLIST.md` R1 pass.

## [1.0.0] — 2026-10-24

MuseDesktop 1.0.0 — the complete 100-enhancement program (slices 1–11, items 1–100) in one release. If you're updating from v0.5.0, everything below is new to you; the per-slice sections ([0.6.0]–[0.14.0], all folded into this release) carry the fine print.

### Added

- **A global show/hide hotkey (#96)** — Ctrl+Alt+M, from anywhere in Windows: press it to bring MuseDesktop forward, press it again to hide it to the tray. Change or turn it off in Settings → Accessibility & keyboard. If another app already owns the combination, Settings says so plainly instead of the key silently doing nothing. While the app is locked the hotkey only ever shows the lock screen, and it never hides the window out from under an open dialog.
- **Quick dictate (#97)** — a tray-menu item and a taskbar Jump List task that show the window with the Muse page already focused, one step from Muse's voice button. If the microphone hasn't been granted to Muse yet, the status line says exactly that instead of pretending to start dictation. MuseDesktop never injects anything into the Muse page — focusing the page is where its part honestly ends.
- **Drag-and-drop guidance (#98)** — dropping files onto the Muse page worked all along (the page's own upload handling receives them); the window frame now lights and the status bar points you at the page while you drag a file over the app, and a drop on the toolbar explains where files belong. The app itself never reads dropped files.
- **musedesktop:// links (#99)** — links can open MuseDesktop at the window, Home, Settings, Downloads, Help, or a named profile (`musedesktop://profile/work`). The route list is fixed and complete: a link can never carry a web address or open anything else, an unknown profile just opens the window and logs why, and while the app is locked, links wait behind the lock and apply after you unlock. The installer registers the scheme; portable installs register it from the Settings toggle, and another app's registration is never overwritten.
- **A complete command line (#100)** — `--profile`, `--background`, `--home`, `--settings`, `--downloads`, `--dictate`, `--safe-mode`, `--help`, and `--version`, all documented in the README's new "Command line & links" section. When MuseDesktop is already running, the flags arrive as actions in the running app (a running app receiving `--settings` opens Settings; `--profile` asks before switching, as always). Desktop shortcuts, Jump List tasks, and automation all use this one surface.
- **A real downloads manager (slice 2)** — parallel downloads with pause/resume, in-session retry, a searchable/filterable history with failure records and a size cap, per-profile download folders, filename-collision choices, a completion balloon (off by default), taskbar progress, and a live speed readout.
- **Profiles, finished (slice 3)** — per-profile desktop shortcuts, rename with a color dot, quick switching from the ⋯ menu and the tray, a per-profile zoom override, data size and last-used in Settings, safe deferred deletion of removed profiles' data, copy-settings on create, and up to 32 profiles.
- **Tray & notification polish (slice 4)** — an attention badge taken from the page title's own count, a rebuilt tray menu (Downloads submenu, live suspend status), a taskbar Jump List, suspend timing choices, quiet hours behind one notification gate, and separate start-with-Windows / start-minimized choices.
- **Settings & appearance (slice 5)** — settings search, reset-to-defaults that demands a backup export first, light/dark/follow-system themes, five accent colors, a customizable toolbar, shell text size, per-profile start pages, and a one-screen first-run wizard you can re-run any time.
- **Accessibility & keyboard (slice 6)** — a full shell accessibility pass (focus visuals, High Contrast support, screen-reader announcements), a searchable Shortcuts window (Ctrl+/), remappable in-app shortcuts with conflict refusal, and finer zoom steps with an optional extended range.
- **Privacy & security (slice 7)** — granular clear-data per profile, a permissions manager and counts-only site-data viewer, encrypted settings export (passphrase-based, built-in crypto only), a redacted-log viewer, a counts-only privacy report, autofill controls, a plain-language connection-security dialog with per-host session-only exceptions, and an optional **app lock**: a password/PIN you set in the app, asked before the window (and Muse) is ever created, with an honest note that it stops casual access and does not encrypt your disk.
- **Performance & reliability (slice 8)** — one recovery coordinator for crashes, network returns, and power resumes; a `--safe-mode` launch and a guided reset wizard; startup timings and a memory readout in Diagnostics; a per-profile data cap that can only ever clear disposable caches; and a hung-page watchdog that offers Reload/Wait and never reloads on its own.
- **Updates & installer (slice 9)** — in-app update download with percent/ETA and SHA-256 verification before Install is even offered, What's New on first run of a version, snooze and check-frequency controls, one-step rollback to the previous version reached through the updater, Stable/pre-release channels, and an installer that remembers your desktop-shortcut choice.
- **Diagnostics, support & help (slice 10)** — the full Diagnostics panel, a preview-first support bundle (redacted log, no addresses, no lock record — nothing uploaded, ever), seven offline Help pages that jump to the fix, guided microphone and notification tests, one-click log actions, a real About window, and an opt-in window screenshot for the bundle.
- **Window & navigation (slice 1, shipped in v0.5.0)** — find in page, print/save-as-PDF, history dropdowns, always-on-top, focus mode, a second window, per-profile window placement, reopen-closed-window, and classified load failures with one automatic retry.

### Notes

- Core test suite grows by 113 cases in this slice (531 → 644). All v1.0.0 runtime behavior is **Unverified** on a real Windows machine pending the consolidated `RELEASE_CHECKLIST.md` pass (the S11 section plus the S1–S10 sections).

## [0.14.0] — Unreleased

Slice 10 (items 89–95) of the 100-enhancement program: diagnostics, support & help.

### Added

- **The full Diagnostics panel (#89)** — the Diagnostics window (⋯ menu) now also shows the app's architecture, your Windows build, the .NET runtime version, the active profile, the update channel, whether the app lock is on, per-area data sizes, and the most recent errors from the log — redacted before they're shown, like everything else that can leave the app. **Copy report** still puts the whole thing on the clipboard in one click.
- **Support-bundle export (#90)** — Diagnostics (and the ⋯ menu) can save one .zip with the diagnostics report, the last 500 log lines (redacted first — never the raw file), your download history, and a settings copy made for sharing: before anything is written, a preview lists every file and states what's NOT in it — no cookies, no sign-in tokens, no page or conversation content, and no web addresses from your settings. The bundle's settings are sanitized harder than a settings export on purpose: the app-lock password record is removed entirely and last-page/start-page addresses are stripped (a test pins both). Nothing is uploaded anywhere; you save the file and choose whether to share it. A `manifest.txt` inside the zip repeats the inventory.
- **In-app Help (#91)** — ⋯ → *Help & troubleshooting…* opens seven offline pages for the problems people actually hit: sign-in loops, voice dictation not hearing you, silent notifications, a missing/too-old WebView2 Runtime, failing downloads, a forgotten app password (with the tested settings.json recovery, spelled out), and pages that won't load. Each page says what you'll see, why, and the steps — and where the fix lives in the app, its button takes you there (Settings at the right section, Diagnostics, the log viewer). Only the "report a problem" link goes online, and it's labeled.
- **Guided microphone + notification tests (#92)** — Settings → Privacy & security gains **Test microphone…** and **Send test notification**. The mic test asks for the microphone through the same WebView2 permission path a page uses (in this profile), then listens for a few seconds: the result says whether permission was granted AND whether sound actually arrived, plus what Muse's saved microphone choice is. The notification test fires MuseDesktop's own tray balloon — sent even during quiet hours or Windows Do Not Disturb because you asked for it, and the result line says plainly when those are holding automatic notifications back. Both results are shown and announced for screen readers.
- **Log actions (#93)** — the log viewer and Diagnostics gain **Open today's log** (the file itself, in your text editor) and **Copy last error** (just the most recent error, redacted) — no more hunting through the folder for the one line a bug report needs.
- **A real About window (#94)** — About is now a window instead of a message box: version, release channel, architecture, Windows/.NET/WebView2 versions, the data paths, a **Check for updates** button that runs the normal update flow, and **Licenses & notices** — MuseDesktop's own MIT license plus the notices for the WebView2 SDK and .NET, shipped beside the app so the installed copy is what's shown.
- **Optional window screenshot in the bundle (#95)** — the bundle preview offers "Include a screenshot of the MuseDesktop window" (off by default). It captures only this app's own window — never your desktop or other apps — shows you the picture next to a warning that it may include conversation text (blur or crop it in Paint first), and the screenshot enters the bundle only if you checked the box and confirmed. While the app is locked, no screenshot is offered at all.

### Notes

- Core test suite grows by 26 cases (505 → 531). All v0.14.0 runtime behavior is **Unverified** on a real Windows machine pending the `RELEASE_CHECKLIST.md` S10 pass.

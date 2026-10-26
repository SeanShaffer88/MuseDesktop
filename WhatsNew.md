# What's new

The release notes MuseDesktop shows on the first launch of a new version (S9 #82). This file is generated from CHANGELOG.md's current-version section at release time (see RELEASE_CHECKLIST.md) — do not edit it by hand; a test pins it to the changelog so the two can't drift.

## [1.5.0] — Unreleased

Round 2, Slice R5 (items 141–150) of the enhancement program: focus & productivity — the final slice of the +50 programme. Everything in this slice is local-only: the timers count minutes on this PC, the lists and tallies live in small files beside your settings, and nothing is ever sent anywhere.

### Added

- **Focus mode + focus timer (#141)** — focus mode (F8) now fills the screen: the main window maximizes and the app chrome hides, as before. Turn on the focus timer in Settings → Focus & productivity and a focus block counts down in the status bar while you work; when it ends, a gentle tray reminder suggests a break — dismiss it and carry on whenever you like. It is never a dialog, never blocks anything, and quiet hours hold it back like every other MuseDesktop balloon. Focus time you actually spend is counted toward the weekly stats.
- **Session timer (#142)** — Pomodoro-style cycles per profile: work / short break, with a long break after a set of work blocks (25 / 5 / 15, every 4 by default — all adjustable in Settings). The countdown lives in the status bar and the tray tooltip; pausing freezes the clock honestly (paused minutes never count as work), and stopping mid-block banks the minutes you did work.
- **Reading list (#143)** — *Save page to reading list* in the ⋯ menu keeps the current page for later: its title and its in-app path only, never the full address (a saved page can't carry a query string or a token). The list is per profile, lives under ⋯ → *Reading list…*, Ctrl+K finds saved pages too, and entries mark read as you finish them. Only Muse pages can be saved — that's what the list is for.
- **Window groups-lite (#144)** — name a set of the windows you have open right now ("Deep work") from the window switcher's right-click menu; the switcher groups them under their name, and choosing the group raises all of them. Groups are deliberately lighter than workspaces: they label live windows and are forgotten when the app exits — nothing is saved.
- **Full-text history search (#145)** — Ctrl+K now searches this session's page history too: every word you type must appear in a page's title or path, duplicates collapse to the most recent visit, and results show titles + paths (never a full address in the list; the page itself opens in the app). MuseDesktop keeps no browsing history between sessions by design, so the search covers the session you're in — the help page says so plainly.
- **Weekly digest (#146)** — ⋯ → *Weekly digest…* builds a plain summary of the week so far — launches, downloads completed, notes taken, pages saved, focus time, your most active profile — from the counts kept on this PC. It is generated only when you ask, shown on a simple page, and sent nowhere.
- **Command scheduling (#147)** — schedule any command-palette action to run at startup, daily at a time, or once at a chosen moment (Settings → Focus & productivity). Schedules run only while MuseDesktop is running — nothing is registered with Windows Task Scheduler, and a daily command whose time passed while the app was closed runs once when you next launch, never in a burst.
- **Quick note capture (#148)** — ⋯ → *Quick note…* (or Ctrl+K) files a note under the profile you're using; *Open notes* shows the notebook. An optional global hotkey (Ctrl+Alt+N by default, off until you turn it on in Settings) opens the capture from any app, under the same rules as the show/hide hotkey — it can never be the same combination as show/hide or as an in-app shortcut.
- **Window tiling presets (#149)** — beyond R1's halves: quarters, thirds, and two-thirds placements for the current window, from Ctrl+K ("Tile: …") and the switcher's right-click menu for any window. The math is the same work-area discipline as the halves — the taskbar is never covered.
- **Stats dashboard (#150)** — ⋯ → *Stats dashboard…* draws the week's counts per profile (launches, downloads completed, focus minutes) as simple bar charts, from the same local tallies the digest reads. *Clear stats data* deletes the tally file; the counts are the only thing it holds — no titles, no paths, no page data.

### Notes

- Core test suite grows by 38 cases (857 → 895). All v1.5.0 runtime behavior is **Unverified** on a real Windows machine pending the `RELEASE_CHECKLIST.md` R5 pass.

## [1.4.0] — Unreleased

Round 2, Slice R4 (items 131–140) of the enhancement program: downloads round 2.

### Added

- **Download sort rules (#131)** — finished downloads can be filed into subfolders by type (Images, Documents, Archives, Audio, Video, Programs, Other) or by your own rules — extensions or a *pattern*, one per line in Settings → Downloads (`pdf, docx = Documents`, `*invoice* = Finance`). The Downloads window's **Sort…** shows the whole plan before anything moves, nothing is ever overwritten (clashes get a " (1)" suffix), every sort is journaled, and **Undo sort** puts files back. Sorting can also run automatically as each download finishes — off by default, because moving files unasked is opt-in.
- **Download scheduling (#132)** — two levers in Settings → Downloads. A bandwidth limit caps all downloads together: WebView2 can't slow a single transfer, so MuseDesktop pauses the newest download while the total is over the limit and resumes it when there's room (a coarse average cap, honestly labelled). Download quiet hours hold KNOWN-large files until the window ends, then start them by themselves; small files — and files whose size isn't known yet — still download. Held links live in memory only and are forgotten if you quit.
- **Duplicate-download guard (#133)** — when a download's name (and size, where known) matches a finished download whose file is still on disk, MuseDesktop asks before downloading it again; keeping both saves the new copy under a fresh name. On by default. Hashing is deliberately NOT part of the guard — that's what Verify SHA-256 is for.
- **Batch link capture (#134)** — **Add links…** in the Downloads window takes a pasted list (one link per line), keeps only http/https links, drops duplicates, shows exactly what will download and what was skipped, then starts them under a cap of 4 running at once; the rest queue. Links are never written to disk, and the log only ever sees host + path.
- **Download speed graph (#135)** — the Downloads window draws the recent history of total download speed (current and peak) above the history list. Samples stay in memory on this PC only — closing the window forgets them.
- **Verify after download (#136)** — **Verify SHA-256…** checks a finished file against a hash you paste (grouped formats with spaces or colons are fine) and records the verdict on its history entry — "SHA-256 verified" or "SHA-256 check FAILED" — so the pass/fail state survives the window closing. The hashing happens on this PC; nothing is sent anywhere.
- **Download notes (#137)** — **Note…** attaches a short note to any history entry ("the signed copy", "for the tax folder"). Notes are stored with the history on this PC only.
- **Archive downloads (#138)** — **Archive…** moves finished downloads into dated folders (one per day they finished) under an archive folder you choose in Settings. Sorting can send old downloads there automatically ("archive downloads older than N days"). Archiving rides the same preview + journal + Undo discipline as sorting.
- **Automatic retry (#139)** — a download that fails for a network or server reason retries itself up to 3 times (Settings: 1–5, or off), waiting 10 s, then 30 s, then 90 s between tries, with each wait announced in the status bar. Disk, permission, and cancelled downloads never retry — they would fail identically.
- **Per-profile download quotas (#140)** — each profile can carry a soft cap on its downloads folder (Settings → Downloads sets it for the profile you're using). Crossing 80% and 100% earns a heads-up in the status bar and the log. The cap never blocks a download and never deletes anything.

### Notes

- Core test suite grows by 97 cases (760 → 857). All v1.4.0 runtime behavior is **Unverified** on a real Windows machine pending the `RELEASE_CHECKLIST.md` R4 pass.

## [1.3.0] — Unreleased

Round 2, Slice R3 (items 121–130) of the enhancement program: profiles round 2.

### Added

- **Profile avatars (#121)** — each profile can wear one of the twelve text glyphs (★ ● ◆ ▲ ■ ♥ ♪ ☀ ☾ ⚑ ✚ ⌂ — the same palette as workspace icons) beside its accent color: the glyph leads the profile's name in the window switcher, the ⋯ menu, and the tray. A profile's desktop-shortcut icon is redrawn from its color + glyph (or its name's first letter) every time the shortcut is created, so changing the avatar and re-creating the shortcut refreshes the icon.
- **Per-profile PIN (#122)** — a profile can have its own PIN or password (Settings → Account profiles), asked when MuseDesktop launches with that profile — after the app password, if one is set: the global lock always gates first. It uses the app lock's exact design (salted PBKDF2, same cost, same fail-open repair if its record is ever damaged), and it carries the same honest promise: a privacy screen for casual glances, not encryption.
- **Scheduled profile switching (#123)** — give a profile a daily window (say Work, 09:00–17:00 on weekdays; a window can run past midnight). When the window starts, MuseDesktop asks before switching — asking is the default; Settings can switch automatically instead. Either way, you win: switching by hand, or saying No, holds the schedule off until the current window ends.
- **Profile clone (#124)** — *Clone profile* copies a profile's settings and appearance into a new profile ("Work" → "Work copy"). Sign-in data is never copied — the clone starts signed out, with no PIN and no schedule of its own.
- **Profile health (#125)** — one counts-only report per profile (Settings → *Profile health…*): data size, disposable cache size, cookie count, last used, last settings backup, data cap. Numbers only — no site names, no domains, no page data — and nothing leaves the PC.
- **Profile export/import (#126)** — export one profile as a small bundle file (optionally password-encrypted, like the settings export) and import it on another PC as a new profile. A bundle carries settings and appearance only — never sign-in data, cookies, the PIN, the schedule, group membership, or usage counts — and importing never overwrites an existing profile.
- **Profile groups (#127)** — named groups gather profiles under a heading in the ⋯ menu, the tray switch list, and Settings, for anyone nearing the 32-profile cap. Deleting a group just ungroups its profiles. The command palette (Ctrl+K) gains "Switch to group:" commands that open the group's most recently used profile.
- **Profile templates (#128)** — quick-create from three starters: **Work** (blue flag, starts at the Muse home page), **Personal** (purple heart, reopens the last page), **Reading** (orange moon, last page at 125% zoom). A template is only a starting point — the profile it makes is ordinary and fully editable.
- **Profile usage (#129)** — Settings shows each profile's launch count and last-launched date. The tallies live in their own small file on this PC only, never ride any export or bundle, and delete in one click (*Clear usage counts*).
- **Archive a profile (#130)** — archiving hides a profile from the switcher, menus, tray, palette, and the Ctrl+Shift profile chords while keeping every byte of its data; untick the box in Settings to restore it. An archived profile's desktop shortcut still opens it (you named it on purpose), and the profile you're currently using can't be archived.

### Notes

- Core test suite grows by 43 cases (717 → 760). All v1.3.0 runtime behavior is **Unverified** on a real Windows machine pending the `RELEASE_CHECKLIST.md` R3 pass.

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

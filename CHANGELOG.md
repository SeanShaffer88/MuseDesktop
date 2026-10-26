# Changelog

All notable changes to MuseDesktop. Format follows Keep a Changelog; versions use semantic versioning.

Versions 0.6.0–0.14.0 below were internal development builds of the 100-enhancement program; they were never published as releases. Everything in them shipped together as **1.0.0** (2026-10-24).

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

## [0.13.0] — Unreleased

Slice 9 (items 81–88) of the 100-enhancement program: updates, installer & distribution.

### Added

- **In-app update download with hash verification (#81)** — the update notice now opens an update window instead of only sending you to the browser: **Download update** fetches the installer for your PC (x64 or ARM64) with percent + speed + ETA, then checks its SHA-256 fingerprint against the release's `SHA256SUMS.txt`. Only a verified installer can be installed, and only when you choose **Install** — nothing is ever downloaded or installed automatically or silently. A fingerprint mismatch deletes the file with a plain explanation; a release without a sums file is refused (this project's releases always include one).
- **What's New (#82)** — the first launch of a new version shows that version's release notes once. Fresh installs don't see it (there's nothing "new" yet), a "Don't show What's New again" box and a Settings switch turn it off, and it never appears before the app-lock password.
- **Update snooze + check interval (#83)** — automatic checks can run every launch (as before), daily, weekly, or never (manual only; the ⋯ menu's *Check for updates* always works). The update window's **Remind me tomorrow** / **Remind me next week** snooze reminders; Settings → Behavior shows an active snooze and can cancel it.
- **Rollback (#84)** — the in-app updater keeps the installer it downloaded; when the next update installs, that installer becomes the rollback: **Diagnostics** gains **Reinstall previous version (x.y.z)**, which runs it in place after a confirm. Honest coverage: rollback exists only for versions that were themselves reached through the in-app updater (the chain starts with the first update after this version) — versions installed from the website or a zip have no kept installer. Exactly one previous version is kept.
- **winget manifest, prepared (#85)** — `packaging/winget/` now holds a ready manifest set for the current published release (v0.5.0, both architectures, with the real installer hashes) plus the exact submission steps. The submission itself to microsoft/winget-pkgs happens only with the maintainer's explicit go-ahead — until then, `winget install MuseDesktop` is **not** live; the installer/zip on the Releases page remain the way to get MuseDesktop.
- **Release channel (#88)** — Settings → Behavior: **Stable releases only** (default) or **Stable + pre-releases**, with the warning spelled out. Stable users are never offered a pre-release; if you try pre-releases and switch back, you're offered the next stable release — never an automatic downgrade.

### Changed

- **Installer audit (#86)** — the installer already offered a folder choice and already reused your folder on upgrade; what was missing was memory for the **Desktop shortcut** checkbox (optional, off by default). The installer now records your choice and pre-selects it on the next install/upgrade, so an upgrade no longer second-guesses it.

### Notes

- **Code signing (#87)** — still **unsigned**: the certificate is the maintainer's purchase decision, and none was made. What this release adds is readiness: a manually-triggered `Sign-Release` workflow that stays inert until two repository secrets and one repository variable exist, plus the enable steps in `RELEASE_CHECKLIST.md`. The day a certificate exists, signing turns on without a code change — and until then SmartScreen's first-run warning remains (see KNOWN_LIMITATIONS).
- Core test suite grows by 44 cases (461 → 505). All v0.13.0 runtime behavior is **Unverified** on a real Windows machine pending the `RELEASE_CHECKLIST.md` S9 pass — in particular the full update loop can only be exercised once a NEWER release exists to update to (see the checklist for what can be checked now vs then).

## [0.12.0] — Unreleased

Slice 8 (items 71–80) of the 100-enhancement program: performance, reliability & recovery.

### Added

- **Crash recovery (#71)** — when the page's renderer process exits, MuseDesktop now reloads the page you're on automatically, once; when the whole web-view (browser) process exits, it rebuilds the web view and restores your page, once. If recovery doesn't take — or the same thing fails again — the familiar failure screen appears instead. Everything else the web engine reports (a GPU or utility process restarting, which WebView2 handles itself) is just logged. The log now says plainly whether a failure was *recovered* or *given up on*, and which process failed.
- **Safe mode + guided reset (#72)** — launch with `--safe-mode` (or hold Shift while launching): MuseDesktop starts at the Muse home page instead of restoring your last page, and skips the automatic update check. The app lock still applies — safe mode is a rescue, not a bypass. Settings → Data also gains **Reset MuseDesktop…**, a three-step guided reset for "won't start / won't sign in": your current settings are backed up first (automatically — and if the backup can't be written, the settings reset won't run), then you choose, each explained: reset settings to defaults, clear site data for all profiles, or both.
- **Startup performance readout (#73)** — every launch now measures process start → window shown → web view ready → first page loaded. The figures land in the log and in the new **Diagnostics** window (⋯ menu), which keeps the last run plus up to ten previous runs.
- **Memory readout (#74)** — Diagnostics shows the memory MuseDesktop and its web-view processes are using right now, and — after the web view has been suspended in the tray and resumed — what suspend actually saved (before/after pair, kept for the session only).
- **Per-profile data cap (#75)** — Settings → Account profiles: an optional cap per profile (off by default; 500 MB / 1 GB / 2 GB). When a profile's data folder passes its cap, cleanup empties **only** disposable cache folders (page, code, and GPU caches — an explicit allowlist), oldest first. **Cookies and sign-in/session data are never touched** — they're on a protected list the cleanup can't include, pinned by tests. Cleanup is never silent: it's logged, stamped on the profile, shown in Settings, and also available on demand as **Clean caches** in the site-data viewer.
- **Network-change resilience (#76)** — the "you're back online" recovery is now one coordinator for everything: network returning, Wi-Fi changing, and the PC waking from sleep all feed the same decision — if the current page is sitting on a load failure, it reloads once. Combined with #71, there's exactly one automatic-recovery budget per incident, so a flapping connection can't cause a reload loop.
- **Proxy readout (#77)** — Diagnostics shows the proxy Windows is configured to use (from your per-user Internet Settings: server hostnames only — credentials are stripped and never shown or logged — plus whether an automatic configuration script/PAC is set). Audited and confirmed: MuseDesktop never overrides the system proxy anywhere; WebView2 uses the Windows setting as-is. Display only — there's no proxy editor.
- **Runtime health check (#78)** — at startup MuseDesktop records the WebView2 runtime's version and warns (log + Diagnostics) if it's missing or older than the supported floor (120.0, the first broadly-deployed Chromium-120-era Evergreen runtime — deliberately conservative), or if a fixed-version runtime folder is found beside the app (MuseDesktop ships Evergreen; a fixed folder changes which runtime you actually get, so it's worth knowing).
- **Rotating backups (#79)** — automatic backups of your settings and downloads history, in a `backups` folder beside your settings: at most one new automatic backup per day (checked on settings saves and at startup), the five most recent of each kind kept. Settings → Data lists them with a **Restore…** each (settings restores go through the same checks as Import), plus a **Back up now** button. This extends — doesn't replace — the reset backup and the `.corrupt` preservation.
- **Hung-page watchdog (#80)** — every 15 seconds a lightweight check asks the page to answer. Two missed answers in a row and a banner offers **Reload the page** / **Wait** — nothing is ever reloaded automatically, because you might be mid-conversation; **Wait** quiets the checks for 2 minutes, and if the page recovers on its own the banner goes away. The checks pause while the web view is suspended in the tray and while the page is playing audio or may be in a voice session.

### Notes

- All automatic recovery (crash, network, sleep-wake) runs through one coordinator in Core (`RecoveryCoordinator`) with one budget: one automatic recovery per incident, refilled only by a page that loads successfully.
- Core test suite grows by 33 cases (428 → 461). All v0.12.0 runtime behavior is **Unverified** on a real Windows machine pending the `RELEASE_CHECKLIST.md` S8 pass.

## [0.11.0] — Unreleased

Slice 7 (items 61–70) of the 100-enhancement program: privacy & security. Everything new lives in a new Settings section, **Privacy & security**, unless said otherwise.

### Added

- **Granular clear-data (#61)** — clearing is no longer all-or-nothing: choose **Cookies only** (signs the profile out), **Cache only** (signs nobody out), or **All site data** (the old behavior), scoped to **this profile only** or **all profiles**. The confirm dialog states in plain words what the chosen option deletes and whether it signs anyone out, before anything happens.
- **Permissions manager (#62)** — the saved permission choices of the profile MuseDesktop is currently running, read live from WebView2, each with **Allow / Block / Forget**. Below it, the app's own recent permission denials (kind + site + time — no page addresses). The app's policy is unchanged and stated in the section: microphone and notifications are granted to muse.ai when the page asks; everything else is denied.
- **Site-data viewer (#63)** — per profile: data-folder size plus cookie count and how many sites they span, and a **Clear this profile…** action. Cookie **values are never read into the UI** — counts and domain names only.
- **App lock (#64)** — an optional password or PIN set inside MuseDesktop (SEAN's design choice — not Windows Hello). Off by default. When on, the password is asked **at startup** (before the main window — and WebView2 — exists, so nothing can render first), from **Lock now** in the tray and ⋯ menus, and when the window hides to the tray (that trigger has its own switch, on by default). Only a salted PBKDF2 hash is stored (210,000 iterations, random salt) — never the password. Five wrong tries pause unlocking for 30 seconds; there is no permanent lockout and no data wipe, ever. While locked, the tray shows no page title, unread count, or download names, and the Downloads window stays behind the lock; downloads already running finish normally. **Honest scope: the lock stops casual access to the open app — it does not encrypt your sign-ins on disk.** Forgotten-password recovery is documented in Settings and KNOWN_LIMITATIONS (delete `settings.json`; the lock goes with it).
- **Encrypted settings export (#65)** — Export offers a password-encrypted file (PBKDF2 + AES-256-GCM, versioned envelope, built-ins only — no new packages). Import detects the envelope and asks for the password; a wrong password and a damaged file get different, plain messages. Choosing the plain export says up front that profile names and start pages in it are readable by anyone who opens the file.
- **Log viewer with redaction check (#66)** — ⋯ menu → *View log…* (and a button in the Privacy section): the log in-app, read-only, with a redaction audit run over exactly what's on screen — it counts anything that looks like a full address with a query, a token, or an email, by line number, honestly (the log's rules already keep addresses host+path only; the audit is the proof). **Copy redacted log** copies the text with anything flagged removed first.
- **Privacy report (#67)** — counts of what MuseDesktop has blocked on this PC (navigations it won't open, blocked pop-ups, denied permissions), by kind and site, with a since-date and a reset button. Counts only: no page addresses, no content, nothing leaves the PC.
- **Password & autofill controls (#68)** — two toggles for WebView2's own password autosave and general autofill (both on, matching WebView2's defaults — the app previously hardcoded them on). Changing them applies to the running profile immediately on OK.
- **Connection security (#69)** — ⋯ menu → *Connection security…*: the current site's address and HTTPS state, certificate details when on HTTPS, and the WebView2 runtime version. Certificate errors now get a plain-language in-app dialog (previously WebView2's built-in error page carried them): what a certificate problem means, which site, the certificate's issuer and validity, **Go back** (the default) or **Continue anyway — this session only, for that site**, with the choice logged. The connection dialog can forget the session's continue-anyway choices.
- **Data-folder shortcut + backup guide (#70)** — Settings → Data: **Open data folder**, and a **Backup guide** that says exactly which folders hold your sign-ins (the per-profile WebView2 folders), what a settings export does and doesn't cover, and the honest limit — WebView2 protects sign-in cookies with your Windows user (DPAPI), so sign-ins copied to a different PC or Windows user will not unlock: Muse will simply ask you to sign in again. Your chats are stored in your Muse account and are never at risk either way.

### Notes

- The old **Clear browsing data and sign out** button in Settings → Data is replaced by #61's granular controls in the Privacy & security section; its behavior (All site data, this profile, sign-out, land on Home) is preserved as the pre-selected choice.
- Privacy counters live in their own small file (`privacy-stats.json`), not in settings — so they never ride a settings export to another PC.
- Core test suite grows by 43 cases (385 → 428). All v0.11.0 runtime behavior is **Unverified** on a real Windows machine pending the `RELEASE_CHECKLIST.md` S7 pass.

## [0.10.0] — Unreleased

Slice 6 (items 51–60) of the 100-enhancement program: accessibility, keyboard & focus.

### Added

- **Accessibility pass over the shell (#51)** — every shell control carries an accessible name (the ⋯ menu's icon-only items, the find bar, the toolbar — now including the two download lists and zoom pickers the pass caught), focus is always visible (an explicit focus-visual style the app applies everywhere, including inside Settings' own local styles), and Windows **High Contrast** is honored: with HC on, the shell paints from the system colors Windows guarantees readable, and accent-filled buttons pair the system highlight with its matching text color. Toggling HC in Windows repaints the shell live.
- **Keyboard shortcuts window (#52)** — **Ctrl+/** (or the ⋯ menu) opens a searchable list of every shortcut MuseDesktop binds. The list is rendered from one catalog in code, so it can't drift from what the keys actually do.
- **Changeable shortcuts (#53)** — Settings → *Keyboard & accessibility*: remap **Open Settings** (default Ctrl+,), **Open Downloads** (new default Ctrl+Shift+D), and **Go to Muse home** (new default Alt+Home). Click a key button and press the new combination; conflicts — with a fixed shortcut or another action — are refused with a plain explanation, and every key can be reset individually or all at once. Honest scope note: MuseDesktop has never had a global (works-anywhere-in-Windows) hotkey — show/hide is the tray icon — so there is no global key to remap; the three in-app actions above are the remappable set.
- **Screen-reader announcements (#54)** — download completed, download interrupted, and update-available events are announced through accessibility live regions, so Narrator/NVDA speak them instead of the news sitting silently in the status bar.
- **Reduce motion (#55)** — Settings → *Keyboard & accessibility* → Reduce motion: Follow Windows (the default; reads the Windows animation-effects setting), On, or Off. Honest surface: the shell has no animations of its own to disable — what this quiets is the taskbar attention flash from v0.8.0. Animation inside the Muse page already follows Windows' own setting.
- **Focus returns after dialogs (#56)** — Settings, Downloads, the shortcuts list, file/folder pickers, and confirm dialogs now give keyboard focus back to whatever invoked them — including back into the web page — instead of dropping you at the top of the window.
- **Finer zoom steps + extended zoom range (#57)** — zoom steps are now 5% apart between 75% and 125% (bigger jumps outside). Settings → Zoom → *Extended zoom range* widens zoom to **25%–500%** for low-vision use, instead of the standard 50%–200%. Per-profile zoom keeps working exactly as before, clamped to whichever range is on.
- **Web-view focus ring (#58)** — when keyboard focus is inside the Muse page, a ring in the accent color appears around the web view; when focus is on the shell chrome, it's gone. The web view also carries the accessible name "Claude web content".
- **High-DPI / multi-monitor placement clamp (#60)** — the app manifest now declares PerMonitorV2 DPI awareness explicitly, and a saved window position is checked against the monitors actually connected (each at its own display scale): a window saved on a monitor that's no longer there comes back on the primary monitor instead of off-screen.

### Notes

- An automated audit (#59) now runs in the test suite: it scans every shell XAML file and fails if an interactive control lacks an accessible name, tooltip, or text — the pass above fixed everything it flagged, and it guards against regressions.
- Core test suite grows by 61 cases (324 → 385). All v0.10.0 runtime behavior is **Unverified** on a real Windows machine pending the `RELEASE_CHECKLIST.md` S6 pass — this slice above all needs the keyboard-only and Narrator runs spelled out there.

## [0.9.0] — Unreleased

Slice 5 (items 41–50) of the 100-enhancement program: settings, appearance & personalization.

### Added

- **Search inside Settings (#41)** — a search box at the top of Settings filters by section: matching sections stay visible with their headers highlighted and the window scrolls to the first match; the rest hide until the search is cleared. (Settings is one scrolling window of sections, so search is section-level — see DECISIONS.md.)
- **Reset all settings to defaults (#42)** — Settings → Data → *Reset all settings to defaults…* After a confirm, your current settings are first exported to a timestamped backup (`settings-backup-*.json` in the settings folder — if the backup can't be written, nothing is reset), then every preference returns to its default. Profiles, sign-ins, last pages, and downloaded files are deliberately untouched; Import brings the backup back.
- **Shell theme (#43)** — Settings → *Appearance & toolbar* → Shell theme: **Dark** (the family black/red identity), **Light**, or **Follow Windows** (follows your Windows app theme, including while the app runs). Shell windows only — toolbar, menus, Settings, Downloads; the Muse page is never restyled.
- **Accent color (#44)** — Red (default), Blue, Teal, Purple, or Orange for the shell's own highlights. Every accent is contrast-checked in code (WCAG AA against both shell themes, tests pin the ratios). Separate from profile colors.
- **Toolbar customization (#45)** — show/hide and reorder the toolbar buttons (Back, Forward, Reload, Home, zoom out / % / in, pin, Open in browser) in Settings → *Appearance & toolbar*, with a Reset layout button. Back always stays; the ⋯ menu is fixed, last.
- **Shell text size (#46)** — 100 / 125 / 150% for MuseDesktop's own text (toolbar, menus, Settings, Downloads), independent of page zoom. The tray menu is drawn by Windows and keeps the system size.
- **Custom startup page per profile (#47)** — each profile now chooses: reopen its last page, start at the Muse home page, or start at a specific Muse page. The address is validated (must be an https:// muse.ai page); an unusable address falls back to the home page with a log line, never to a broken page.
- **Settings remembers itself (#48)** — Settings reopens at the section you left, at the size you left it (the window is resizable now).
- **Portable mode (#49)** — put an empty `portable.txt` beside `MuseDesktop.exe` (zip build) and settings, download history, and logs live in a `data` folder beside the exe instead of `%APPDATA%`. Nothing is moved automatically — a portable launch starts fresh there. Sign-in data (the WebView2 profiles) still lives in your user profile in this version.
- **First-run setup wizard (#50)** — a fresh install opens with one welcome screen: close-to-tray, start-with-Windows, downloads folder, update checks — pre-filled, skippable, and never shown twice. Re-run it any time from Settings → Behavior → *Run the setup wizard again…*

### Notes

- Core test suite grows by 56 cases (268 → 324). All v0.9.0 runtime behavior is **Unverified** on a real Windows machine pending the `RELEASE_CHECKLIST.md` S5 pass.

## [0.8.0] — Unreleased

Slice 4 (items 31–40) of the 100-enhancement program: tray, notifications & presence.

### Added

- **Attention badge (#31)** — when Muse's page title carries an unread count in the standard "(N)" prefix form, the tray icon and the taskbar button show that count as a badge, and the tray tooltip names it. No title count, no badge — the app never invents a number. If Muse doesn't use that title convention, this simply never appears.
- **A complete tray menu (#32)** — Open, New window, Reload, **Downloads** (the manager window plus your five newest finished files, click to open), **Settings…**, **Switch profile**, a status line showing the web view's suspend state, and Exit. A hidden MuseDesktop is fully operable without opening it.
- **Taskbar Jump List (#33)** — right-click the taskbar button: *Open Muse*, *Settings* (opens the app with Settings up, via a new `--settings` flag), and your most recently used profiles under *Profiles*. Like the per-profile shortcuts, a profile task can't switch an already-running instance (single-instance still just wakes the window).
- **Configurable tray suspend timeout (#34)** — Settings → *Suspend after hidden for*: 1, 5 (as before), or 15 minutes, or Never.
- **No suspend over a possible voice session (#35)** — once the microphone has been granted to Muse in a session, tray suspend stays out of the way until the page navigates. Honest limit: WebView2 exposes no "microphone is capturing right now" signal (verified against the pinned SDK), so this is a deliberate heuristic — see DECISIONS.md — and WebView2's own suspend can still decline on its own for live media.
- **Single-click tray restore (#36)** — a Settings option: one click on the tray icon reopens the window instead of a double-click.
- **Tray icon profile tint (#37)** — the tray icon carries a small dot in the active profile's accent color (bottom-left; the attention badge sits top-right, so both can show).
- **Quiet hours (#38)** — MuseDesktop's own balloons and the attention flash respect Windows Do Not Disturb (best-effort read of the system state) plus an in-app schedule (Settings → *Tray & notifications*, whole hours, wraps midnight). When something is kept quiet, the log says so — nothing is suppressed silently. Muse's own in-page notifications are not affected.
- **Flash on attention (#39)** — off by default: when Muse's title shows a count while you're not looking at the window, the taskbar button flashes gently a few times. It never steals focus.
- **Start-in-tray is its own toggle (#40)** — *Start with Windows* and *When starting with Windows, open in the tray* are now separate. The sign-in launch can open the window instead; double-clicking the app always opens the window. The default keeps the long-standing behavior (sign-in launches land in the tray).

### Notes

- Core test suite grows by 45 cases (223 → 268). All v0.8.0 runtime behavior is **Unverified** on a real Windows machine pending the `RELEASE_CHECKLIST.md` S4 pass.

## [0.7.0] — Unreleased

Slice 3 (items 23–30) of the 100-enhancement program: profiles & accounts.

### Added

- **Per-profile desktop shortcuts (#23)** — Settings → *Account profiles* → *Create desktop shortcut* puts a shortcut on the Desktop that opens MuseDesktop with that profile (`--profile <id>` — the app's command line is now parsed in Core, unknown arguments tolerated; an unknown id falls back to your usual profile with a log line). Like a Settings switch, the launched profile becomes the active one.
- **Rename + accent colors (#24)** — profiles can be renamed in Settings and given an accent color from a fixed palette (Red, Blue, Green, Purple, Orange, Gray). The color shows as a dot next to the profile's name in Settings, the ⋯ menu, the tray menu and tooltip, and the window title line.
- **Quick profile switcher (#25)** — the ⋯ menu and the tray menu both have a *Switch profile* submenu (active profile checked, accent dots shown) that runs the same restart-and-switch as Settings — no Settings trip needed.
- **Per-profile zoom (#26)** — a profile can carry its own zoom instead of the shared default: Settings → *Account profiles* → *Use a custom zoom for this profile*. While a profile has a custom zoom, zooming in the app (Ctrl +/−/0, toolbar) updates that profile's zoom; the shared default is untouched.
- **Profile data size + last used (#27)** — Settings shows each profile's on-disk data size and the date it was last launched with.
- **Deferred folder deletion (#28)** — when a removed profile's data folder can't be deleted at removal time (in use, or the delete fails), the deletion is queued and retried automatically at the next launch, instead of the folder staying on disk forever.
- **Copy settings on create (#29)** — *Add profile* can copy an existing profile's settings (start page, downloads folder, zoom, color). Sign-ins and session state are never copied — the new profile starts signed out with its own empty data folder.
- **Profile cap raised to 32 (#30)** — up from 8. Past 8 profiles, adding another asks first (each profile keeps its own sign-in data on disk).

### Notes

- Core test suite grows by 29 cases (194 → 223). All v0.7.0 runtime behavior is **Unverified** on a real Windows machine pending the `RELEASE_CHECKLIST.md` S3 pass.

## [0.6.0] — Unreleased

Slice 2 (items 13–22) of the 100-enhancement program: downloads.

### Added

- **Parallel downloads manager (#13)** — the Downloads window now opens with an *Active downloads* list: every in-flight download with its own progress, ETA, speed, and controls. The status bar keeps the exact single-download wording when one file runs and switches to an aggregate ("Downloading 3 files — 42%") when several do; clicking the status text while downloads run opens the manager.
- **Pause / resume (#14)** — Pause and Resume buttons in the manager, riding WebView2's own `Pause()`/`Resume()`/`CanResume` (verified present in the pinned SDK 1.0.4258.31). A paused download shows as *Paused*, never as a failure.
- **Retry failed downloads (#15)** — a download that fails can be re-issued from the manager **during the same session**: the app navigates a hidden WebView2 on the same sign-in to the failed address and takes over the new download. The address is held in memory only — never written to disk or logged in full — so the retry offer ends when the app exits.
- **History search, filter, sort, and a configurable cap (#16)** — search by file name, filter All / Completed / Failed, sort by date, size, or name. Failed downloads are now recorded in history (with the failure reason). Settings → Downloads chooses how much history to keep: 50 (as before), 200, or 1000 entries.
- **History file actions (#17)** — *Copy path*, *Open with…*, and *Delete file* (confirmed; deletes the file and removes its history entry) join Open / Show in folder.
- **Completion notification (#18)** — optional Settings toggle (off by default): when a download finishes while the window is hidden in the tray, a tray balloon names the file. No sound.
- **Taskbar progress (#19)** — the Windows taskbar button mirrors overall download progress (normal while transferring, yellow when only paused downloads remain, red after a failure until the next download starts, cleared when idle).
- **Per-profile downloads folder (#20)** — each profile can override where its downloads land (Settings → Account profiles → select a profile → Browse); the shared folder remains the fallback.
- **Filename-collision handling (#21)** — if the target file exists, the download is saved as "name (1).ext" automatically, or — with the new Settings choice — you're asked where to save that file instead.
- **Download speed readout (#22)** — a smoothed transfer speed ("1.5 MB/s") alongside % and ETA in the manager and in the status-bar detail. The status line's main wording is unchanged; the one intentional change is the speed appended to its right-hand detail (pinned by new Core tests; the previous `ProgressStatus` tests are untouched and still pass).

### Notes

- Core test suite grows by 51 cases (143 → 194). All v0.6.0 runtime behavior is **Unverified** on a real Windows machine pending the `RELEASE_CHECKLIST.md` S2 pass.

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

## [0.5.0] — 2026-10-24

Slice 1 (items 1–12) of the 100-enhancement program: window & navigation.

### Added

- **Find in page (Ctrl+F)** — a find bar (match count, previous/next, Esc to close) built on WebView2's `CoreWebView2.Find` API, present in the pinned SDK 1.0.4258.31.
- **Print (Ctrl+P) and Save as PDF** — ⋯ menu / Ctrl+P opens WebView2's print UI; *Save as PDF…* writes a PDF via `PrintToPdfAsync` to a file you choose.
- **Back/Forward history dropdowns** — right-click or long-press Back/Forward to jump through this session's pages (new Core `NavigationHistory` model, capped at 50, consecutive duplicates collapsed).
- **⋯ menu: Copy page address and Open in browser** acting on the current page.
- **Always-on-top pin** — toolbar 📌 button or ⋯ menu toggle, persisted (`AlwaysOnTop`, off by default).
- **Focus mode (F8)** — hides the toolbar and status bar for a distraction-free window; F8 or Esc exits. Session-only, never persisted; separate from F11 full screen.
- **Open in new window** — a lightweight second window on the same WebView2 environment/profile (same sign-in), for viewing a second page alongside the main window.
- **Per-profile window placement** — each profile remembers its own window size/position/maximized state; existing installs migrate their current placement into the default profile automatically.
- **Ctrl+Shift+T** — restore the last page you navigated away from (or the page showing when a load failed / the web view crashed).
- **Smarter load-failure messages** — failures are classified in plain language (offline, server unreachable, DNS, certificate, other) instead of a raw error code, and the app retries once automatically when your network connection changes back.
- **Window title shows the profile** — with more than one profile, the title bar/taskbar reads "page — profile — MuseDesktop".

### Notes

- Middle-click / Ctrl+click link audit (item 11): those gestures reach the app through the same WebView2 new-window path as every other link, so the existing link policy already covers them; Core tests now pin that classification for every host class. No behavior change.
- Core test suite grows by 34 cases (110 → 144). All v0.5.0 runtime behavior is **Unverified** on a real Windows machine pending the `RELEASE_CHECKLIST.md` S1 pass.

## [0.4.0] — 2026-10-23

### Added

- **ARM64 builds.** Releases now ship native ARM64 downloads (`MuseDesktop-v0.4.0-windows-arm64.zip` and `MuseDesktop-Setup-v0.4.0-arm64.exe`) alongside x64 — built and smoke-tested on GitHub's ARM64 Windows runners, so Snapdragon/Windows-on-ARM PCs get a native app instead of the emulated x64 one.
- **Download history.** The ⋯ menu's new **Downloads…** window lists finished downloads (newest first, kept on this PC only): name, size, date, and location, with **Open** and **Show in folder** actions and a **Clear history** button. Until now the only record of a download was the status-bar message, which the next download erased.
- **Per-profile start page.** Each profile can now start at the Muse home page instead of reopening its last page: Settings → *Account profiles* → select a profile → *Start this profile at the Muse home page*.
- **Settings export / import.** Settings → *Data* can save the settings (including profiles and per-profile start pages) to a JSON file and load them back — handy for backups and moving to a new PC. Imports are validated and sanitized like any settings load. Sign-ins are not exported; they stay in each profile's own data.
- **Ctrl+, opens Settings** (the Windows convention), and the ⋯ menu now labels it.

### Changed

- **Internals: the main window was decomposed.** Download tracking, the tray icon + suspend timer, and the update check moved out of `MainWindow.xaml.cs` (1,064 → 923 lines) into focused `DownloadTracker`, `TrayController`, and `UpdateService` classes, and the download status wording moved into Core (`DownloadText`) where it is unit-tested. No intended behavior changes; two deliberate micro-fixes ride along: the tray menu's *Reload* now does nothing before the web view is ready instead of failing, and settings adopted from the Settings window now include the per-profile last-page map (import completeness).
- **Keyboard audit:** the shortcut set (Ctrl+R/F5, Ctrl+±/0, Alt+←/→, F11) was audited and found complete and already documented — this release's only keyboard changes are the Ctrl+, addition and its menu label above.

### Notes

- Core test suite grows 86 → 110 (download history store, status wording, per-profile startup, settings export/import).
- The x64 installer is now named `MuseDesktop-Setup-vX.Y.Z-x64.exe` so the two architectures are unmistakable on the Releases page.

## [0.3.1] — 2026-10-23

### Fixed

- **Session restore is now per profile.** In v0.3.0 the "reopen the last page" setting was shared across profiles, so switching accounts reopened the *other* account's last Muse page — wrong-account content or a dead chat link, since chat URLs are account-scoped. Each profile now restores its own last page (`LastUrlByProfile` in settings); the old shared value migrates into the default profile on first load. New Core tests cover migration, isolation, and invalid-entry cleanup (suite now 86).

### Added

- `RELEASE_CHECKLIST.md` gains a consolidated **catch-up pass**: one ordered ~25-minute on-machine session that clears the accumulated v0.2.0–v0.3.1 runtime-verification backlog.

### Notes

- Evaluated and **not** adopted: single-file compressed publishing. Measured locally, the single-file exe is 69.8 MB versus the current 70.3 MB zip — a saving too small to pay for the extraction startup cost and WebView2-loader risk. The v0.3.0 satellite trim remains the size story.
- No other behavior changes. This is also the update notifier's first live test: v0.3.0 installs should offer this release in the status bar.

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

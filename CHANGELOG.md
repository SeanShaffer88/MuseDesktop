# Changelog

All notable changes to MuseDesktop. Format follows Keep a Changelog; versions use semantic versioning.

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

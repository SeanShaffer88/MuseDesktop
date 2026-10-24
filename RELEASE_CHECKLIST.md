# Release checklist

Run before publishing any release. CI covers the automated half; this is the on-machine half that automation can't prove — it exists because v0.1.0 passed every automated check and crashed on first launch.

## Catch-up pass — v0.2.0 + v0.3.0 + v0.3.1 behaviors (one sitting, ~25 minutes)

The per-release items below accumulated faster than on-machine passes ran. This ordered pass clears the whole backlog against the current release; after it, delete this section and rely on the per-release items again.

1. Install the current release with the **installer** (per-user, no admin prompt). Launch from the finish page; sign in.
2. **Update notifier loop:** install v0.1.3 from its release, launch once — the status bar should offer v0.2.0+ and the ⋯ item should open the release page. Then install current: ⋯ → *Check for updates* reports "up to date".
3. **Downloads:** have Muse produce a file. Watch % + ETA; cancel with ✕ and confirm the partial file/state is sane; download again, click the completion line to open the file. In Settings, set a custom downloads folder → next file lands there; enable *Ask where to save* → Save-As appears, and cancelling it cancels the download.
4. **Settings window:** toggle close-to-tray off → window close quits; back on → hides to tray with the one-time balloon. Start-with-Windows on → sign out/in of Windows → app arrives in the tray; off → Run entry removed. Change default zoom → applies on OK; reopen, change, Cancel → nothing changed.
5. **Tray suspend:** hide to tray, wait 6+ minutes, reopen — log shows "suspended" then "resumed"; send a message to confirm the page is alive.
6. **Clear data:** Settings → *Clear browsing data and sign out* → confirm → Muse asks for sign-in; sign back in; chats intact.
7. **Profiles:** add a "Work" profile, switch (app restarts) — separate sign-in; browse to a chat; switch back to Default — the *original* profile restores *its own* last page, not Work's (v0.3.1). ⋯ menu shows "Profile: …"; tray tooltip names the active profile.
8. **Context menu:** right-click a link in a chat → *Open link in browser* works; *Copy link address* pastes the URL.
9. **Base behaviors:** tray Open/Reload/Exit + double-click; second launch wakes the window; voice dictation; F11 + Esc; resize/move → relaunch restores placement and zoom.
10. **Installer lifecycle:** run the installer again (upgrade) → still signed in; uninstall answering **No** → program gone, data remains; reinstall, uninstall answering **Yes** → `%APPDATA%\MuseDesktop` and profile folders gone.
11. **Screenshots** (README list): ⋯ menu, download in flight, tray menu, Settings window — supply to the maintainer for the README.
12. **Log review:** `%APPDATA%\MuseDesktop\logs\musedesktop.log` — startup lines present, no ERROR entries beyond explained ones.

## Automated (CI must be green first)

- [ ] Build (Release) — 0 errors
- [ ] Test (core logic) — full suite, 0 failures
- [ ] Smoke test — published exe constructs the real main window **and** initializes WebView2 (exit 0)
- [ ] Publish + artifact uploaded

## On a real Windows machine (use the release zip, fresh profile where noted)

- [ ] Unzip to a clean folder, launch: window opens, no error dialog, Muse sign-in page loads
- [ ] Sign in, quit, relaunch: still signed in, last page restored
- [ ] Tray: icon present; Open / Reload / Exit all work; double-click reopens
- [ ] Second launch while running: existing window comes to the front, no duplicate
- [ ] **Settings →** Close window to tray on → closing hides to tray with the one-time balloon; off → app exits
- [ ] **Settings →** Start with Windows on → sign out/in to Windows → app arrives quietly in the tray; off → Run key value removed
- [ ] **v0.2.0 — Downloads:** start a download, cancel it from the status bar (✕); download again and click the completion line to open the file; in Settings set a custom downloads folder and confirm the next file lands there; enable *Ask where to save* and confirm the Save-As dialog appears and cancelling it cancels the download
- [ ] **v0.2.0 — Settings:** change default zoom in Settings → OK → zoom applies; reopen Settings → Cancel after a change → nothing changed
- [ ] **v0.2.0 — Clear data:** Settings → *Clear browsing data and sign out* → confirm → Muse asks for sign-in again; chats intact after signing back in
- [ ] **v0.2.0 — Tray suspend:** hide to tray, wait 6+ minutes, reopen: window returns, page works (send a message); log shows "suspended" then "resumed" lines
- [ ] **v0.2.0 — Update notice:** run the *previous* release (v0.1.3) once this release is published: status bar offers the update and the ⋯ item opens the release page; ⋯ → *Check for updates* on the new build reports "up to date"
- [ ] **v0.3.0 — Profiles:** Settings → add a "Work" profile → switch: app restarts, Muse asks for sign-in; sign in with a different account; switch back to Default: original session intact. ⋯ menu shows "Profile: …" and the tray tooltip names the profile
- [ ] **v0.3.0 — Profiles:** remove the non-active profile → OK → its `WebView2-<id>` folder under `%LOCALAPPDATA%\MuseDesktop` is gone
- [ ] **v0.3.0 — Context menu:** right-click a link in a chat → *Open link in browser* opens it externally; *Copy link address* puts the URL on the clipboard
- [ ] **v0.3.0 — Installer:** run `MuseDesktop-Setup-vX.Y.Z.exe`: installs without admin, app launches from the finish page, Start Menu shortcut works; Apps & Features lists MuseDesktop with the right version
- [ ] **v0.3.0 — Installer:** run the installer again (upgrade): completes over the existing install, still signed in afterwards
- [ ] **v0.3.0 — Installer:** uninstall answering No to data deletion: program gone, `%APPDATA%\MuseDesktop` settings remain; uninstall again answering Yes: data folders gone too
- [ ] **v0.4.0 — Downloads window:** download a file, then ⋯ → *Downloads…*: the file is listed with size/date/path; *Open* opens it; *Show in folder* selects it in Explorer; double-click also opens; *Clear history* (confirmed) empties the list but leaves the files; reopening the window after another download shows both
- [ ] **v0.4.0 — Per-profile start page:** Settings → select a profile → check *Start this profile at the Muse home page* → OK; switch to that profile: it opens at Muse home even though it had a last page; uncheck → last page restores again
- [ ] **v0.4.0 — Settings export/import:** Settings → *Export settings…* → file saved; change a setting (e.g. zoom), *Import settings…* the file → controls show the exported values → OK applies them; importing a non-settings file shows the plain error and changes nothing
- [ ] **v0.4.0 — Keyboard:** Ctrl+, opens Settings from the main window; ⋯ menu shows the "Ctrl+," label on Settings…
- [ ] **v0.4.0 — Refactor regression sweep:** tray balloon on first close-to-tray; suspend after 6 min hidden + resume (log lines); tray Reload works; update check from ⋯ reports correctly; a download's progress/cancel/complete flow matches the pre-refactor behavior
- [ ] **v0.4.0 — ARM64 (if an ARM device is available):** run `MuseDesktop-Setup-vX.Y.Z-arm64.exe`: installs, launches, signs in; otherwise record as CI-verified only
- [ ] Download a file Muse produced: status bar shows % and ETA, file lands in Downloads, completion is reported
- [ ] Click an ordinary web link in a chat: opens in the default browser, not in the app
- [ ] Voice: microphone is granted to muse.ai without extra prompts; dictation works
- [ ] Zoom Ctrl + / − / 0 and F11 full screen (Esc exits)
- [ ] Resize + move, quit, relaunch: placement and zoom restored
- [ ] Check the log (`%APPDATA%\MuseDesktop\logs\musedesktop.log`): startup line present, no ERROR entries
- [ ] Capture the README screenshots (list in README → Screenshots) if the UI changed

Record the outcome per item (pass / fail / not run) in the release notes working file. A "not run" item is reported as unverified — never silently treated as a pass.

## v0.5.0 — Slice 1 (window & navigation) on-machine items

- [ ] **S1 #1 Find:** Ctrl+F opens the find bar; typing shows a live match count (e.g. "3 of 12"); Enter/Shift+Enter move between matches and the page scrolls/highlights; Esc closes the bar and clears highlighting
- [ ] **S1 #2 Print/PDF:** Ctrl+P and ⋯ → *Print…* open the print UI; ⋯ → *Save as PDF…* saves a readable PDF of the current page to the chosen file
- [ ] **S1 #3 History dropdowns:** visit several pages, right-click Back — earlier pages are listed, clicking one jumps to it; same for Forward after going back; long-press (hold ~½ s) Back also opens the list and does not navigate back on release
- [ ] **S1 #4 Menu items:** ⋯ → *Copy page address* pastes the current page URL; ⋯ → *Open in browser* opens it in the default browser
- [ ] **S1 #5 Pin:** 📌 toolbar button and ⋯ → *Always on top* keep the window above other apps; state survives a relaunch (persisted)
- [ ] **S1 #6 Focus mode:** F8 hides toolbar + status bar; F8 and Esc each exit; closing/reopening the app does not leave focus mode stuck on
- [ ] **S1 #7 Second window:** ⋯ → *Open in new window* opens the current page already signed in; both windows work independently; closing the second window leaves the app running; real Exit from the tray closes everything
- [ ] **S1 #8 Per-profile placement:** move/resize the window in Default, switch to another profile, move/resize differently, switch back — each profile restores its own placement; an upgraded install's existing placement appears for the default profile
- [ ] **S1 #9 Restore:** navigate A → B, press Ctrl+Shift+T — page A reopens; after a failed load, Ctrl+Shift+T reopens the failed page
- [ ] **S1 #10 Load failures:** go offline and reload — the overlay says you're offline in plain words (no raw error code); reconnect — the page retries once automatically without clicking Retry; disconnect again mid-failure — no retry loop
- [ ] **S1 #11 Middle/Ctrl+click:** middle-click (and Ctrl+click) an in-app Muse link — stays in-app; an external web link — opens in the default browser; a blocked-scheme link — refused
- [ ] **S1 #12 Title:** with one profile the window title is "page — MuseDesktop"; with 2+ profiles it is "page — profile name — MuseDesktop" in the title bar, taskbar and Alt-Tab

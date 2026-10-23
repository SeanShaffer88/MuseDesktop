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
- [ ] Download a file Muse produced: status bar shows % and ETA, file lands in Downloads, completion is reported
- [ ] Click an ordinary web link in a chat: opens in the default browser, not in the app
- [ ] Voice: microphone is granted to muse.ai without extra prompts; dictation works
- [ ] Zoom Ctrl + / − / 0 and F11 full screen (Esc exits)
- [ ] Resize + move, quit, relaunch: placement and zoom restored
- [ ] Check the log (`%APPDATA%\MuseDesktop\logs\musedesktop.log`): startup line present, no ERROR entries
- [ ] Capture the README screenshots (list in README → Screenshots) if the UI changed

Record the outcome per item (pass / fail / not run) in the release notes working file. A "not run" item is reported as unverified — never silently treated as a pass.

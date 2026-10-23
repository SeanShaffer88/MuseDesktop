# Release checklist

Run before publishing any release. CI covers the automated half; this is the on-machine half that automation can't prove — it exists because v0.1.0 passed every automated check and crashed on first launch.

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
- [ ] Download a file Muse produced: status bar shows % and ETA, file lands in Downloads, completion is reported
- [ ] Click an ordinary web link in a chat: opens in the default browser, not in the app
- [ ] Voice: microphone is granted to muse.ai without extra prompts; dictation works
- [ ] Zoom Ctrl + / − / 0 and F11 full screen (Esc exits)
- [ ] Resize + move, quit, relaunch: placement and zoom restored
- [ ] Check the log (`%APPDATA%\MuseDesktop\logs\musedesktop.log`): startup line present, no ERROR entries
- [ ] Capture the README screenshots (list in README → Screenshots) if the UI changed

Record the outcome per item (pass / fail / not run) in the release notes working file. A "not run" item is reported as unverified — never silently treated as a pass.

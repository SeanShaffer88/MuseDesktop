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
- [ ] **⋯ menu:** Close to tray on → closing hides to tray with the one-time balloon; off → app exits
- [ ] **⋯ menu:** Start with Windows on → sign out/in to Windows → app arrives quietly in the tray; off → Run key value removed
- [ ] Download a file Muse produced: status bar shows % and ETA, file lands in Downloads, completion is reported
- [ ] Click an ordinary web link in a chat: opens in the default browser, not in the app
- [ ] Voice: microphone is granted to muse.ai without extra prompts; dictation works
- [ ] Zoom Ctrl + / − / 0 and F11 full screen (Esc exits)
- [ ] Resize + move, quit, relaunch: placement and zoom restored
- [ ] Check the log (`%APPDATA%\MuseDesktop\logs\musedesktop.log`): startup line present, no ERROR entries
- [ ] Capture the README screenshots (list in README → Screenshots) if the UI changed

Record the outcome per item (pass / fail / not run) in the release notes working file. A "not run" item is reported as unverified — never silently treated as a pass.

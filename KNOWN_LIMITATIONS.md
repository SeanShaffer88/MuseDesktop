# Known limitations

Honest list of what MuseDesktop does not do or has not proven yet. Kept current as work proceeds.

## Verification status

- **Verified on a real Windows machine (2026-10-23, v0.1.2):** startup (clean log, WebView2 ready), sign-in, and voice — confirmed by the user with the app log. (v0.1.0 crashed at startup; v0.1.1 fixed it; v0.1.2 is the first fully verified line.)
- **Still unverified at runtime:** tray menu items exercised end-to-end, close-to-tray, Start-with-Windows autostart, a completed download with progress/ETA, and F11 full screen — these run through `RELEASE_CHECKLIST.md` on the next on-machine pass. CI green alone is never treated as "it works".
- **New in v0.2.0, also runtime-unverified:** the update notice appearing (and the ⋯ item opening the release page), download cancel / click-to-open / Save-As prompt / custom downloads folder, clear-browsing-data sign-out, and WebView2 suspend-after-5-minutes-in-tray with clean resume. All are covered by new `RELEASE_CHECKLIST.md` items.
- **New in v0.3.0, also runtime-unverified:** profile add/switch/remove (including the restart hand-off), the link context-menu items, and the NSIS installer (install, upgrade-in-place, uninstall with both data choices). Covered by new `RELEASE_CHECKLIST.md` items.

## Behavior

- **Single window, no tabs.** Muse's own UI is the tab strip; links that would open a tab follow the link policy (in-app hosts replace the page, everything else goes to your browser). There is no back-history beyond WebView2's own.
- **F11 full screen** is handled by the app window; when keyboard focus is inside the web content, some keys are consumed by the page first (standard WebView2 accelerator behavior). Pages that request full screen themselves (e.g. video) are honored via `ContainsFullScreenElementChanged`.
- **Downloads** show one operation at a time (the most recent) in the status bar, with % + ETA, a cancel button, and click-to-open when finished (all since v0.2.0); there is still no pause/resume and no multi-download manager. Files land in your Downloads folder unless you pick another folder or enable ask-where-to-save in Settings.
- **Clearing data** is available in Settings (*Clear browsing data and sign out*) since v0.2.0; a partial clear (cookies only, cache only) is not offered.
- **Tray suspend:** after 5 minutes hidden in the tray the web view is suspended to free memory (Settings can turn this off). Suspend is a WebView2 feature — a page in the middle of something (e.g. a voice call) can decline it, and streaming replies pause while suspended; everything resumes when you reopen the window.
- **Profiles (v0.3.0):** switching profiles restarts the app (a WebView2 environment is bound to its data folder for the process lifetime). The profile you're currently running can't have its data folder deleted from Settings — removing it unlists it, and its folder stays on disk (delete `%LOCALAPPDATA%\MuseDesktop\WebView2-<id>` by hand if you want it gone). Settings like zoom and downloads are shared across profiles; the sign-in/session data and (since v0.3.1) the restored last page are per-profile.
- **Installer (v0.3.0):** the NSIS installer is per-user (no admin, no UAC) and unsigned, like the zip — SmartScreen may warn on first run. Installing over an existing install (upgrade) replaces the program files in place; your data folders are never touched by an upgrade.
- **Notifications** depend on Muse's web app requesting them and on Windows notification settings; the app grants the permission but cannot make Muse use it.
- **Unsigned binary.** SmartScreen will warn on first run until/unless a code-signing certificate is adopted.
- **Self-contained download (tens of MB).** Since v0.1.2 the .NET 10 runtime is bundled, so there is no .NET prerequisite — the cost is download size and that runtime patches arrive via app releases rather than the OS. Distribution is still a plain zip (no installer), x64 only.
- **WebView2 Runtime is a prerequisite** on the minority of Windows 10 PCs that lack it; the app detects this and links to Microsoft's Evergreen installer but does not install it for you.

## Documentation

- **README screenshots are partially complete.** The main-window shot (real v0.1.2 run, conversations blurred) is wired in; the remaining captures (⋯ menu, download progress, tray menu) are listed in the README's Screenshots section and land as they're captured. No placeholder imagery is used.

## Accessibility notes

- Toolbar buttons carry tooltips and AutomationProperties names, and zoom level is text (not color-only). A full screen-reader pass over the shell and a keyboard-only end-to-end run are still outstanding.

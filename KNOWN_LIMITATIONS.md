# Known limitations

Honest list of what v0.1.0 does not do or has not proven yet. Kept current as work proceeds.

## Unverified (release gate)

- **No on-machine Windows run yet.** CI compiles the app and runs the core-logic tests on a Windows runner, but nobody has launched the exe on a real Windows 10/11 PC: sign-in flow, tray behavior, close-to-tray, autostart, downloads, voice permission, and full-screen are all **unverified at runtime** until that first manual session. Do not treat CI green as "it works".

## Behavior

- **Single window, no tabs.** Muse's own UI is the tab strip; links that would open a tab follow the link policy (in-app hosts replace the page, everything else goes to your browser). There is no back-history beyond WebView2's own.
- **F11 full screen** is handled by the app window; when keyboard focus is inside the web content, some keys are consumed by the page first (standard WebView2 accelerator behavior). Pages that request full screen themselves (e.g. video) are honored via `ContainsFullScreenElementChanged`.
- **Downloads** cannot be cancelled or paused from the status bar yet, and only one download's progress is shown at a time (the most recent). Files always land in the default Downloads folder; there is no per-download Save-As prompt (the WebView2 default UI is intentionally suppressed).
- **No in-app "clear browsing data / sign out" button yet.** To reset the session, exit the app and delete `%LOCALAPPDATA%\MuseDesktop\WebView2`.
- **Notifications** depend on Muse's web app requesting them and on Windows notification settings; the app grants the permission but cannot make Muse use it.
- **Unsigned binary.** SmartScreen will warn on first run until/unless a code-signing certificate is adopted.
- **Self-contained download (tens of MB).** Since v0.1.2 the .NET 10 runtime is bundled, so there is no .NET prerequisite — the cost is download size and that runtime patches arrive via app releases rather than the OS. Distribution is still a plain zip (no installer), x64 only.
- **WebView2 Runtime is a prerequisite** on the minority of Windows 10 PCs that lack it; the app detects this and links to Microsoft's Evergreen installer but does not install it for you.

## Documentation

- **README screenshots are outstanding.** Per project convention, screenshots come from a real run on SEAN's Windows machine; the capture list lives in the README's Screenshots section. No screenshots are included until those captures exist — the section is a list, not placeholder imagery.

## Accessibility notes

- Toolbar buttons carry tooltips and AutomationProperties names, and zoom level is text (not color-only). A full screen-reader pass over the shell and a keyboard-only end-to-end run are still outstanding (they need the on-machine session above).

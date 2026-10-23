# Decisions

A living log of significant architecture, UX, security, and dependency decisions.

## 2026-10-23 — The v0.2.0 pack: Settings window, update check, download controls, clear data, tray suspend

SEAN picked the full v0.2.0 bundle from the improvement shortlist. Decisions inside it:

- **Update channel = GitHub Releases.** The public repo's `/releases/latest` endpoint is the single source of truth; the app compares its assembly version against the tag (Core `UpdateInfo`, unit-tested). One request per launch, 12 s timeout, User-Agent identifies the app; every failure path is a log line, never UI. Pre-release tags never count as updates. No third-party updater framework — at this app's scale a notifier + link is honest and sufficient, and auto-install would need signing/update plumbing deliberately out of scope.
- **Settings window edits a clone** (`AppSettings.Clone()`), OK adopts, Cancel discards — the forgiving-UI standard. The ⋯ menu's checkable toggles were removed so settings have exactly one home; the menu keeps actions (Settings…, Check for updates, log folder, About).
- **Clear data uses the WebView2 profile API** (`Profile.ClearBrowsingDataAsync(All)`) rather than deleting the profile folder: the folder delete races the running controller and can't happen in-process; the API is the platform's supported reset and keeps the profile structure intact.
- **Suspend uses WebView2's own `TrySuspendAsync`** after 5 minutes hidden (not a process freeze): the platform knows how to sleep a renderer safely and resume it; the app honors a decline, never suspends mid-download, and resumes before showing the window.
- **Downloads stay single-operation in the status bar** (see KNOWN_LIMITATIONS) but gained the controls users actually reach for: cancel, click-to-open, folder choice, Save-As. A multi-download manager UI stays deferred until there's evidence one download at a time hurts.

## 2026-10-23 — Close minimizes to tray by default (v0.1.3)

v0.1.2 passed its on-machine verification (clean startup log, sign-in and voice confirmed by SEAN). His direction from that session: **closing the window minimizes to the tray by default** — MuseDesktop is a resident companion app, so the X means "put it away," and Exit lives on the tray menu where it always has. The default applies to fresh installs; existing installs keep their saved preference (settings are per-user and the ⋯ toggle is one click). If real-world feedback says the default confuses people, it's a one-line change in `AppSettings`.

## 2026-10-21 — Public product; .NET 10; self-contained download (v0.1.2)

SEAN set the audience to **public product** (not personal tool) and approved the review's top recommendations. Consequences:

- **.NET 8 → .NET 10 LTS.** Microsoft ends .NET 8 support on 2026-11-10; an app that hosts a browser and holds a signed-in session must not sit on an unpatched runtime. .NET 10 LTS runs to November 2028. `global.json` pins the SDK band (10.0.x, latestFeature) so builds are reproducible.
- **Self-contained publish replaces framework-dependent.** A public first-run cannot dead-end on a missing .NET Desktop Runtime with a generic OS dialog. The zip grows from ~0.7 MB to tens of MB (the publish folder measures 175 MB before compression); in exchange the only remaining prerequisite is the WebView2 Runtime, which the app already detects with a guided path. Trade-off accepted knowingly: the bundled runtime is patched by shipping new app releases rather than by the OS, which makes keeping releases current part of the deal.
- **DevTools is Debug-only** — a release build should not hand a scripting console to a signed-in session.
- **Runtime verification is a release gate, not a hope:** the CI smoke test now requires WebView2 to initialize, and `RELEASE_CHECKLIST.md` defines the on-machine checks. This is the structural answer to the v0.1.0 startup crash, which every compile/test check missed.

## 2026-10-20 — C# / WPF + WebView2 instead of the usual Rust default

Muse is a website, so the client is a shell around a web view. On Windows the first-party tool for that is Microsoft's WebView2 (Edge/Chromium) with its official .NET SDK: documented APIs for the exact things this app needs — a persistent user-data folder (login session), new-window and navigation events, permission requests, and download operations with progress and ETA.

The Rust alternative (wry/tao) abstracts those away behind a cross-platform API whose surface changes noticeably between minor versions, and exposes downloads/permissions only partially. The concrete advantage — a stable, Microsoft-maintained SDK for the one platform this app targets — outweighs the standing Rust default for this project. Plain logic (URL policy, settings, zoom) lives in a dependency-free `MuseDesktop.Core` library so it stays unit-testable anywhere, and the WPF shell stays thin.

Alternatives considered: Tauri (Rust + WebView2, but a large toolchain/JS build for a single-window shell); Electron (bundles a whole Chromium — hundreds of MB to show one website, and a second browser profile to secure); a PWA shortcut (no tray, no download progress, no link policy — the behaviors that justify a native client at all).

## 2026-10-20 — Framework-dependent publish, Evergreen WebView2

The release is a framework-dependent win-x64 publish (~a few MB) that requires the .NET 8 Desktop Runtime and the Evergreen WebView2 Runtime (preinstalled on Windows 11 / most Windows 10). This keeps downloads small and lets Microsoft service the web engine's security fixes independently of this app. Trade-off: two runtime prerequisites, both detected with a plain-language explanation in-app when missing (WebView2) or by Windows itself (.NET).

## 2026-10-20 — Link and permission policy

- Top-level navigation: any HTTPS page may load (sign-in and connector OAuth flows redirect across providers). HTTP and non-web schemes are refused.
- New-window requests: only Muse and known identity-provider hosts (`muse.ai`, `meta.com`, `facebook.com`, `instagram.com`, `accounts.google.com`, `appleid.apple.com`, `login.microsoftonline.com`, exact host or true subdomain) stay in-app. All other http(s) links open in the user's default browser. Look-alike hosts never match.
- Permissions: auto-grant only `notifications` and `microphone`, only for origin `https://muse.ai`. Everything else is denied and logged. Rationale: notifications are how a long-running agent says "done", and the microphone is Muse's voice input; a desktop client that silently granted camera/geolocation to arbitrary origins would be a liability.

## 2026-10-20 — Downloads handled in the status bar

WebView2's default download flyout is suppressed (`DownloadStarting` → `Handled`) and progress is reported in the app status bar as percent + ETA from the download operation, because that matches how the rest of SEAN's apps report progress and keeps the single-window app self-contained. Files land in the default Downloads folder (WebView2's default path).

## 2026-10-20 — Black/red product-family look

Toolbar and status bar use the family tokens (near-black surfaces `#0B0B0D`/`#141417`, hairline `#2C2C31`, red accent `#E50914`, off-white text), and the Windows title bar is set to immersive dark via `DwmSetWindowAttribute`. The web content itself is Muse's own design and is not restyled — injecting CSS into a third-party app would break with their updates and misrepresent their product.

## 2026-10-20 — Settings format

One JSON file, schema-versioned, written atomically (temp file + replace). A file that fails to parse is copied aside as `settings.json.corrupt-<timestamp>` and defaults are used, so user data is never silently discarded. The WebView2 bootstrapper link used in the missing-runtime message is Microsoft's official fwlink (`https://go.microsoft.com/fwlink/p/?LinkId=2124703`, the Evergreen bootstrapper referenced in Microsoft's WebView2 distribution docs).

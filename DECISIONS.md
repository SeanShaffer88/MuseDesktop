# Decisions

A living log of significant architecture, UX, security, and dependency decisions.

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

# MuseDesktop

A native Windows desktop client for [Muse](https://muse.ai) — Meta's personal AI agent.

Muse ships apps for iOS, Android and macOS, and a web app at muse.ai — but no Windows app. MuseDesktop fills that gap: a small, focused Windows program that keeps Muse one click away, signed in, with the desktop behaviors a browser tab can't give you.

> MuseDesktop is an independent open-source project. It is not made by, endorsed by, or affiliated with Meta. "Muse" is a product of Meta; this app simply displays muse.ai in a native window.

## What you get

- **A real app, not a tab** — its own window, taskbar icon, and Alt-Tab entry; no browser chrome competing for space.
- **Stay signed in** — a dedicated WebView2 profile on your PC keeps your Muse session between launches, and the app reopens the last Muse page you were on.
- **Tray support** — always-available tray icon (Open / Reload / Exit); optional *Close window to tray* and *Start with Windows* (quiet tray launch) in the **⋯** menu.
- **Downloads with progress** — files Muse makes for you download into your Downloads folder with live progress % and an estimated time remaining in the status bar, instead of a browser flyout.
- **Sensible link handling** — Muse and sign-in pages stay in the app; ordinary web links from chats open in your default browser; dangerous schemes (`file:`, `javascript:`, …) are refused.
- **Voice and alerts** — Muse origins get microphone (voice dictation) and notification permission; every other permission request is denied rather than silently granted.
- **Keyboard-friendly** — Back/Forward/Reload/Home in the toolbar, zoom controls with a % readout (Ctrl + / − / 0), F11 full screen, and standard browser accelerators inside the page.
- **Private by construction** — no telemetry, no accounts of its own, no data collection. The only files it writes are its settings, a rolling local log, and the WebView2 profile (cookies/local storage) — all under your user profile, all deletable by you at any time.
- **Forgiving** — second launches wake the running window instead of opening a duplicate; a corrupted settings file is preserved as a backup and defaults are used; load failures explain themselves and offer a retry.

## Requirements

- Windows 10 or Windows 11 (x64)
- .NET 8 Desktop Runtime (the release zip is framework-dependent)
- The Microsoft Edge **WebView2 Runtime** — preinstalled on Windows 11 and most Windows 10 PCs. If it's missing, MuseDesktop tells you and links you to Microsoft's installer.

## Install

1. Download `MuseDesktop-vX.Y.Z-windows-x64.zip` from the [Releases](../../releases) page and unzip it anywhere.
2. Run `MuseDesktop.exe`.

The app is not code-signed yet, so Windows SmartScreen may warn on first run — choose **More info → Run anyway**. To uninstall, exit the app and delete its folder; your data folders (`%APPDATA%\MuseDesktop` and `%LOCALAPPDATA%\MuseDesktop`) can be deleted too, which also signs you out.

## Where things live

| What | Where |
| --- | --- |
| Settings | `%APPDATA%\MuseDesktop\settings.json` |
| Diagnostic log | `%APPDATA%\MuseDesktop\logs\musedesktop.log` (1 MB, single rollover; URLs are logged host-and-path only, never with query strings) |
| Web profile (login session) | `%LOCALAPPDATA%\MuseDesktop\WebView2` |
| Downloads | Your normal Downloads folder (WebView2 default) |

## Keyboard shortcuts

| Keys | Action |
| --- | --- |
| Alt+Left / Alt+Right | Back / Forward |
| Ctrl+R or F5 | Reload |
| Ctrl+Plus / Ctrl+Minus | Zoom in / out |
| Ctrl+0 | Reset zoom to 100% |
| F11 | Full screen (Esc exits) — works from the window chrome and the page's own full-screen requests are honored |

## Building from source

Requires the .NET 8 SDK.

```powershell
dotnet restore MuseDesktop.sln
dotnet build MuseDesktop.sln -c Release
dotnet test tests\MuseDesktop.Core.Tests -c Release
dotnet publish src\MuseDesktop\MuseDesktop.csproj -c Release -r win-x64 --self-contained false
```

Architecture: `MuseDesktop.Core` holds all testable logic (URL policy, settings, zoom, paths, logging) as plain .NET 8 with no Windows dependencies; the WPF app in `src\MuseDesktop` is a thin shell over Microsoft's WebView2 control. See [DECISIONS.md](DECISIONS.md) and [KNOWN_LIMITATIONS.md](KNOWN_LIMITATIONS.md).

## License

MIT — see [LICENSE](LICENSE).

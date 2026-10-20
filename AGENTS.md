# AGENTS.md

Notes for agents working on this repo.

- This project follows SEAN's Universal Windows Desktop Master Prompt (kept in his workspace as `windows-desktop-master-prompt.md`): native Windows feel, accessibility as a requirement, honest Verified / Partially verified / Unverified labeling, a decision log (DECISIONS.md) and a living known-limitations list (KNOWN_LIMITATIONS.md) — update both as behavior changes.
- Standing defaults: MIT license, black/red product-family look, Windows 10 support when feasible, progress % + ETA where progress is shown, no competitor comparisons in public docs.
- Deliberate deviation (see DECISIONS.md, 2026-10-20): C# / WPF + Microsoft's WebView2 SDK instead of Rust — the documented reason is the first-party SDK for a single-platform WebView shell. Do not "fix" the stack back to Rust without revisiting that decision with the user.
- Layout: `src/MuseDesktop.Core` = testable pure logic (.NET 8, no Windows APIs). `src/MuseDesktop` = WPF shell (net8.0-windows). Keep new logic in Core with tests unless it truly needs the shell.
- Gate before publishing: `dotnet build MuseDesktop.sln -c Release` with zero errors, and the FULL core test suite (`dotnet test tests/MuseDesktop.Core.Tests -c Release`) — never a filtered subset.
- Dependencies are pinned in the csproj files (WebView2 SDK 1.0.4258.31). Bump deliberately, with the WebView2 release notes checked, not by reflex.
- Never log a full URL: use `UrlPolicy.RedactForLog` (host + path only). Never log page content, cookies, or tokens.

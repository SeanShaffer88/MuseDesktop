# Test coverage map

This map helps keep feature work tied to both automated evidence and the real-Windows release pass. A Core test or CI smoke result does not prove UIA, display, WebView2 runtime, installer, or tray behavior on a user's desktop.

## Automated Core suite

| Area | Main test files | Examples of contracts |
|---|---|---|
| App settings, paths, logging, atomic persistence | `CoreBehaviorTests.cs`, `AtomicFileTests.cs` | Sanitization, path-safe profiles, log redaction, atomic replacement and failure cleanup |
| Tray notifications and activity history | `S4Tests.cs` | Quiet-hours policy, bounded local notification summaries, clear-history behavior |
| URL policy and protocol routing | `UrlPolicyTests.cs`, `S11Tests.cs` | Allowed/blocked schemes, redaction, command-line and URI handling |
| Downloads | `S2Tests.cs`, `R4Tests.cs` | History bounds, concurrent sync/async metadata updates, filename collisions, batch parsing, retry policy, hash checks, sort/undo plans and injected move failures |
| Profiles and data management | `S3Tests.cs`, `R3Tests.cs` | Profile identity, data facts, import/export, schedules, archive/restore |
| Workspaces and sessions | `R2Tests.cs` | Apply plans, restore decisions, workspace contracts, bounded/sanitized snapshot and journal tab routes, active-tab indexes, and legacy fallbacks |
| Accessibility and XAML labels | `S6Tests.cs` | Declared-control labels, high-DPI math, keyboard/focus policy, long-list virtualization |
| Reliability and diagnostics | `S8Tests.cs`, `S10Tests.cs` | Recovery decisions, native unresponsive-renderer prompt policy, versioned local-data backup validation, support-bundle privacy/limits, diagnostic report composition |
| Updates and release metadata | `S9Tests.cs`, `V040Tests.cs` | Hash manifests, asset naming, version and file-content contracts |
| Round 2 features | `R1Tests.cs`–`R5BTests.cs` | Palette search across notes, download annotations, bookmark/snippet text, groups, scheduled-command deadlines, downloads, productivity, conversation-tool contracts |

The authoritative test command is `dotnet test tests/MuseDesktop.Core.Tests -c Release`.

## Windows-only or real-device evidence

Keep the detailed procedures in `RELEASE_CHECKLIST.md`. In particular, run the Narrator/keyboard-only pass, visual theme and mixed-DPI matrix, WebView2 Runtime availability/version checks, update/install/rollback path, and x64/ARM64 retail-device checks. Record pass, fail, or not run; never infer a pass from a Core test.

## Performance evidence

Use `PERFORMANCE_BASELINE.md` for repeatable measurements. Baseline values are not collected in CI and must be recorded from a real Windows device before claiming a performance regression or improvement.

# Cross-program improvement pass

This is new, unreleased work after the twenty Dual workbench additions in v2.3.0.

## Architecture and constraints

MuseDesktop is a .NET 10 WPF shell around pinned WebView2. Core holds portable policy, persistence, search, downloads, speech and settings logic; the Windows layer owns windows, native integration and browser services. The main window remains a large coordinator, so new attachment lifecycle code is isolated in a partial rather than adding another asynchronous subsystem to it.

The review covered those boundaries and their current tests, not just Dual mode. Existing atomic persistence, download retry/verification and profile isolation already cover many generic “enhancement” ideas; they were not reimplemented. This pass preserves Windows 11 x64 only, existing defaults, English/Spanish catalogs and pinned dependencies. No new packages. No external provider's server behavior is changed.

## The twenty changes

| # | Change and reason | Principal area |
|---|---|---|
| 1 | Reject credential-bearing navigation and non-HTTPS/opaque blob origins; keep ordinary HTTPS and trusted blob downloads. Refuse insecure app-host popups. | `Core/UrlPolicy` |
| 2 | Auto-grant microphone/notifications only to the exact default-port Muse HTTPS origin, never embedded credentials or alternate ports. | `Core/UrlPolicy` |
| 3 | Redact opaque/data/file URL payloads instead of logging private contents or local paths. | `Core/UrlPolicy` |
| 4 | Bound encrypted backups to 32 MiB plaintext and wipe derived keys/plain byte buffers in finally blocks. Preserve the existing envelope format. Fix the randomized tamper fixture to mutate decoded bytes, not JSON escapes. | `Core/EncryptedExport`, `S7Tests` |
| 5 | Skip malformed release records/assets and drafts without losing valid later releases; normalize invalid sizes. | `Core/Updates` |
| 6 | Refuse conflicting duplicate checksum entries rather than trusting the last line; identical duplicates remain compatible. | `Core/Updates` |
| 7 | Move attachment scanning off the UI thread, cancel superseded scans and guard results against locking/closing/settings changes; isolate its lifecycle. | `MainWindow.QuickAttach` |
| 8 | Keep only the newest K attachment records during discovery, deduplicate source folders and cache extension metadata. Preserve ordering and cancellation. | `Core/RecentFiles` |
| 9 | Accept familiar shortcut input such as `Ctrl++`, `Ctrl+Enter`, digits, Page Up and PgDn; make displayed names round-trip. | `Core/Accessibility` |
| 10 | Recover update scheduling after a backward system-clock correction; continue honoring snooze and manual-only preferences. | `Core/Updates` |
| 11 | Reset download speed baselines after backward clock changes or counter resets, instead of showing stale rates. | `Core/Downloads` |
| 12 | Format byte sizes correctly with comma decimal separators and handle infinite/overflowing speed inputs safely. | `Core/Downloads` |
| 13 | Show hours/days for long download ETAs, with matching English/Spanish shell output. | `Core/Downloads`, `DownloadLines`, catalogs |
| 14 | Give prompts visible character counts/limits, resizable multiline editing and Ctrl+Enter save; never truncate existing over-limit text silently. Default ceiling is one million characters; Dual names/transfers use their existing tighter limits. | `TextPromptWindow`, `DualWindow.Workbench` |
| 15 | Marshal announcements/history to the registered UI dispatcher and tolerate shutdown races. | `Announcer` |
| 16 | Restore focus even when a dialog throws; avoid disabled/nonfocusable controls and hidden main windows. | `FocusReturn` |
| 17 | Find accented pin titles, conversation history and download filenames/notes without requiring accents; URL/profile identity rules are untouched. | `SearchText`, pins, history, downloads |
| 18 | Choose 4/8/12 recent attachment files and optionally exclude Screenshots. Keep existing defaults, sanitize saved values, expose named keyboard-accessible controls and settings-search terms. | settings model, Settings window, main integration |
| 19 | Accept explicit CSS `#RGB` accent shorthand while keeping canonical `#RRGGBB` and existing contrast enforcement. | `Core/AppearanceRound2` |
| 20 | Respect Markdown code-fence character and length so nested/mismatched fences cannot accidentally make code spoken. | `Core/SpeechText` |

## Verification

`CrossProgramEnhancementsTests` names E01–E20 and exercises public Core behavior or explicit Windows source contracts as appropriate. Existing tests remain part of the full suite, including localization, keyboard traversal and privacy contracts. The Windows smoke path now opens the real Settings surface, updates attachment options, and checks a real multiline dialog rejects oversized text without truncation and accepts valid text. Existing main/Dual browser smoke and DOM fixtures remain.

The final Windows CI run is the authoritative build, complete-suite, native smoke and installer result. Local cross-build, full Core suite, actionlint, whitespace checks and the source-derived JavaScript fixtures are also run. Automated smoke does not establish a full keyboard-only or Narrator pass, high-contrast/mixed-DPI behavior, or live provider composer compatibility; those remain manual Windows checks.

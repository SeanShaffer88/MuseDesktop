# What's new

The release notes MuseDesktop shows on the first launch of a new version (S9 #82). This file is generated from CHANGELOG.md's current-version section at release time (see RELEASE_CHECKLIST.md) — do not edit it by hand; a test pins it to the changelog so the two can't drift.

## [2.4.0] — 2026-11-11

### Twenty cross-program improvements

1. Reject credential-bearing navigation URLs and unsafe blob origins.
2. Require the exact Muse HTTPS origin for automatic permissions.
3. Keep opaque URL payloads and local file paths out of diagnostic logs.
4. Limit encrypted export plaintext to 32 MiB and clear temporary key/plain-byte buffers; preserve escaped Base64 compatibility.
5. Skip malformed release records and drafts without losing valid updates.
6. Refuse conflicting duplicate checksum entries.
7. Discover recent attachments off-thread with cancellation and lock/close guards.
8. Retain only the newest requested attachment files during scanning.
9. Accept friendly shortcut names, digits and plus-key notation.
10. Recover scheduled update checks after backward clock corrections.
11. Reset stale download-speed baselines after clock or byte-counter changes.
12. Correct locale-specific byte formatting and invalid/overflowing speeds.
13. Show localized hours and days for long download estimates.
14. Add resizable text prompts, character counts/limits and Ctrl+Enter saving.
15. Marshal announcements safely to the UI thread and tolerate shutdown races.
16. Restore dialog focus reliably, including exception paths.
17. Search pins, history and downloads without typing accents; include download notes.
18. Choose 4/8/12 recent attachment files and optionally exclude Screenshots.
19. Accept CSS #RGB accent shorthand while preserving contrast enforcement.
20. Keep nested or mismatched Markdown code fences silent during read-aloud.

### Verification and compatibility

- Added 37 regression cases and expanded native Settings/prompt smoke checks. The enhancement pass passed all 1,706 tests and Windows CI; every release reruns the full suite and native startup checks.
- Windows 11 x64 only. No new dependencies or package-version changes beyond the app/Core release version. Full keyboard-only/Narrator, high-contrast and mixed-DPI desktop verification remains manual.
- See CROSS_PROGRAM_ENHANCEMENTS.md for the architecture, file map, rationale and verification limits. The previously published v2.3.0 tag and assets remain unchanged.

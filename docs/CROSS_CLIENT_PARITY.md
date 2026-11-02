# Cross-client parity — MuseDesktop ↔ ArenaDesktop

The two desktop clients share a shell lineage (ArenaDesktop began as a
fork of MuseDesktop). This ledger keeps their shared shell from
drifting apart silently: every capability that exists in one client
carries a recorded decision for the other. Update this file in the
same change that adds a shell capability to either client.

States: **Has it** · **Port planned** (a named release or slice) ·
**Not applicable** (the other product has no use for it, with the
reason) · **Decide** (no decision recorded yet).

| Capability | MuseDesktop | ArenaDesktop | Decision / note |
| --- | --- | --- | --- |
| Shell layouts: Standard / Compact Focus / Left Rail | Has it (2.0.0) | Has it (0.2.0) | Ported 2026-11-02 with the same Core layout model and the same default rules (Compact Focus for new installs, saved choice always kept). |
| Approval alerts (beep + toast) | Has it (1.9.0) | Not applicable | Arena has no approval cards; its pages never ask the user to approve an action. |
| Page-health check (Diagnostics) | Has it (2.0.0) | Decide | Arena's page-dependent surface is smaller (mode routes only); a route-health check is the Arena-shaped version of this idea. |
| Conversation tools (bookmarks, drafts, export) | Has it | Not applicable | These target the Muse page's conversation structure; Arena pages have no equivalent surface the shell may touch (the shell never injects into Arena pages). |
| Arena modes registry + Ctrl+1…9 jumps | Not applicable | Has it | Muse has no modes; its equivalent jumps are profile/window commands. |
| Battle Lab (prompt library, run queue, results journal) | Not applicable | Has it (0.2.0) | Arena-specific: model battles have no Muse equivalent. MuseDesktop's snippet library covers its own prompt-reuse need. |
| Guided field pass | Has it (2.0.0) | Has it (0.2.0) | One design, per-app check lists; shipped in both on 2026-11-02. Opened from Diagnostics in MuseDesktop, from the ⋯ menu and Diagnostics in ArenaDesktop. |
| Profiles, downloads manager, settings search, app lock, in-app updates with rollback, support bundle | Has it | Has it | Inherited at the fork and kept in step; a fix in one is a candidate fix in the other — check here before assuming. |
| Retired-wording guard for public docs | Decide | Has it (0.2.0) | ArenaDesktop guards "Quick dictate" (retired name). MuseDesktop has the WhatsNew↔changelog drift guard; a wording guard is a candidate port. |

## How to use this file

- Adding a shell capability to one client? Add its row here in the
  same change, with the decision for the other client.
- Fixing a shell bug in one client? Check the row for that area; if
  the other client has the same code, the fix is a candidate port —
  record what was decided either way.
- "Decide" rows are open questions for the project owner, not
  permission to port or to skip silently.

# Optional one-time Approval-card automation

Settings → Profiles → select the profile → **Auto-accept one-time Approval cards for this profile**. Read the warning, choose Yes, then Apply/Save. It is off by default. Disable it in the same place; a queued/already-dispatched authorization cannot be undone.

The scope is all recognized one-time operation types, **not** a read-only allowlist. File changes, messages and other consequential actions may be authorized without review. Only enable this for chats you trust.

## Recognition and boundaries

- Main tabs, both Dual panes (their own profile settings), Quick Ask, Compare and secondary windows participate.
- Every two seconds the native host checks live consent, app/profile lock, visibility, exact `https://muse.ai` origin and Home/chat route. It must be able to open the Default Windows input desktop; lock screen, UAC secure desktop or access failure pauses attempts.
- An approval-marked container or explicit approval heading must contain a single recognized one-time action and rejection alternative, without editable fields. One scan stages it; a second after at least 1.5 seconds must see the same card. The action must still be enabled, visible, in the viewport and not covered.
- Recognized labels include Allow once, Allow this time, Approve once, Approve this request, Approve and corresponding supported Spanish labels. Bare Allow/Permitir is eligible only beside a separate permanent choice. Generic Yes and unknown labels remain manual.
- Never clicks Always allow, selects permanent scopes, fills forms or answers browser/native Windows permissions. Quoted code/markdown, ambiguous/multiple matches, credential/authentication pages and iframes are excluded.
- Attempted action/card nodes are not retried; bounded recent request-ID/content digests suppress identical rerenders. A provider reusing the same node or indistinguishable request needs manual approval. New route changes restart settling, not permission for an old already-clicked action.

The original beep/toast attention watcher is unchanged and remains observe-only. This module installs no autonomous page timer and receives no enablement from page messages. Only fixed status strings return to native code; card text and private duplicate fingerprints remain ephemeral in the page, never logs, exports or disk.

An ordinary local save/restart preserves consent; settings/backup imports, cloned/copied profiles, imported bundles and preference reset do not. No dependencies were added.

## Verification

`ApprovalAutoAcceptTests.cs` covers policy gates, opt-in persistence and import/copy/reset boundaries, controller coverage and observer separation. `node scripts/test-auto-approval.mjs` executes the shipped script against controlled DOM fixtures, including permanent/ambiguous requests, settlement, opt-out, duplicates/rerender, route/origin changes, forms, quotes and overlays. Both CI and Release execute it. Native Windows smoke verifies selected-profile binding, accessible name/help text and disabling without a warning.

**Partially verified:** automated fixture and host checks are not a live signed-in Muse integration test or a manual screen-reader/secure-desktop test. Unknown provider markup fails closed; do not infer remote success from a logged dispatch.

# Twenty Dual workbench enhancements

Implemented for v2.3.0, Windows 11 x64 only. This is an implementation
and verification map, not a claim that a retail Windows interaction
pass has been completed. The full Core suite and hosted startup smoke
are release gates; live Muse, Narrator and mixed-DPI checks remain
manual and are recorded separately in RELEASE_CHECKLIST.md.

| # | Enhancement | Entry point / behavior | Manual acceptance |
|---|---|---|---|
| 1 | Swap panes | Swap panes; moves layout, not URLs | Both unsent drafts survive |
| 2 | Independent profiles | Each pane's profile picker; PIN-gated environment | Separate accounts, cancelled PIN loads nothing |
| 3 | Saved chat pairs | Save/Open/Delete pair, up to 20 named presets | Correct routes, profiles and zoom reopen |
| 4 | Stacked layout | Stack / side by side; row/column divider | Resizing works in both orientations |
| 5 | Expand active pane | Expand / restore, Ctrl+Alt+Enter | Other chat stays live and returns unchanged |
| 6 | Detach pane | Move existing WebView2 into owned window | Close to return without losing draft |
| 7 | Active indicator | System-highlight border and active pane identity | Focus always matches highlighted pane |
| 8 | Pane shortcuts | Ctrl+Alt+1/2, R, N, P, Enter; Ctrl+K pane commands | Commands affect only the active chat |
| 9 | Conversation titles | DocumentTitleChanged updates headers, not disk | Title changes follow navigation |
| 10 | Open link in other pane | Native link context-menu item, URL policy checked | Safe link opens opposite side; dangerous URI blocked |
| 11 | Transfer selected text | Review dialog names source/destination profiles | Appends to one composer, no automatic send |
| 12 | Shared scratchpad | Expand explicit local plaintext notes | Notes persist, chat drafts do not enter notes |
| 13 | Reply completion badges | Opt-in recognized stop-control transition | Background completion once, no idle false positives |
| 14 | Find across both | Native WebView2 find, separate counts, active next/previous | Correct independent results, covered panes skipped |
| 15 | Scroll synchronization | Opt-in proportional scroll messages | No feedback loop or editable-field scrolling |
| 16 | Downloads in Dual | Shared destination/collision/duplicate/progress manager | Safe downloads for each profile |
| 17 | Independent recovery | Recover pane rebuilds one view with generation guards | Other live view unchanged unless shared process failed |
| 18 | Complete window restoration | Opt-in reopen, safe routes, layout/zoom and monitor-clamped bounds | Exit/relaunch and disconnected-monitor recovery |
| 19 | One-pane privacy curtain | Hide native view and title, reveal explicitly | No native content visible in embedded/detached window |
| 20 | Accessibility improvements | Named content, polite UIA live regions, focus border, high-contrast tools | Narrator, keyboard-only and High Contrast pass |

## Safety and limitations

- Compare mode remains distinct. Dual never broadcasts or sends.
- Profile selection or pair loading can discard unsent text and asks
  first. Live swap/expand/detach do not recreate or navigate a browser.
- Optional page tools are Muse Home/chat-only with a second document
  origin check. Unsupported composer/streaming/scroll markup fails
  safely; these are not APIs guaranteed by Muse.
- Pair names, safe routes, profile IDs, layout/zoom, opted-in placement
  and explicit scratchpad notes live in dual-sessions.json. Queries,
  titles, transcripts, selections, search terms and composer drafts
  are not automatically persisted. Notes are plaintext, not encrypted.
- Other-profile downloads cannot be queued/retried through the main
  account's hidden browser; retry from the original pane. All accepted
  operations still use the shared manager's safety policies.
- Startup restoration never bypasses locks or safe mode. Detached
  windows are not restored separately. Covers are not password locks.

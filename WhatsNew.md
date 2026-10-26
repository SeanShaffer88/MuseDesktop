# What's new

The release notes MuseDesktop shows on the first launch of a new version (S9 #82). This file is generated from CHANGELOG.md's current-version section at release time (see RELEASE_CHECKLIST.md) — do not edit it by hand; a test pins it to the changelog so the two can't drift.

## [1.6.0] — Unreleased

Round 2 Slice R5B (the follow-on six, items 201–206): conversation
tools. This slice deliberately reaches one step further than every
slice before it — with SEAN's explicit approval, the app now adds
small, user-chosen touches to the Muse page it hosts: styling the
conversation view, watching the message box for drafts, and reading
the conversation back for export. Everything is local: nothing is
sent anywhere, nothing about your chats is logged, and every touch
degrades quietly (the feature simply doesn't happen) if the page's
shape changes under it. See KNOWN_LIMITATIONS for the honest list.

### Added

- **#201 Conversation scrollbars.** The conversation view's
  scrollbar is always visible, wide, and easy to grab — no more
  hunting for the thin one that only appears while scrolling.
  Per profile, on by default; turn it off in Settings → Account
  profiles.
- **#202 Message bookmarks.** Every message gets a small bookmark
  button on hover. A bookmark is a placeholder for that one
  message, kept in this app only — and **saving one requires a
  nickname**: you'll always be asked to name it first, and the save
  can't complete without a name. The new **Bookmarks** button on the
  window's left edge opens the list (nickname, conversation, date);
  clicking a bookmark returns to that conversation and finds +
  highlights the message — or tells you plainly when it can't be
  found (it may have been edited). Rename and delete live in the
  list, and bookmarks are in Ctrl+K too. Stored per profile in
  `bookmarks.json`, as conversation paths only.
- **#203 Export a conversation.** ⋯ menu (and Ctrl+K): **Export
  conversation as Markdown…** reads the open conversation's
  messages and writes a tidy Markdown file — title, date, You /
  Muse headings, code kept as code. **…as PDF…** prints the
  conversation page to a PDF file, as it appears.
- **#204 Drafts autosave.** What you've typed in a conversation's
  message box is kept per conversation and put back when you
  return — after switching away, reloading, or restarting the app.
  Sending the message clears its draft. On by default; the switch
  is in Settings → Account profiles. Drafts live in `drafts.json`
  on this PC only and are never logged.
- **#205 Reading comfort.** Per profile, in Settings → Account
  profiles: chat text size (50–200%), reading-column width, and
  line spacing — applied to the conversation view when you press
  OK, separately from page zoom.
- **#206 Prompt snippet library.** Save prompts you reuse (name +
  text) per profile in Settings, then insert one into the message
  box from Ctrl+K or the ⋯ menu's **Insert snippet** list. Inserting
  never sends — the text lands in the box for you to edit. Stored
  in `snippets.json`.

Bookmarks, drafts, and snippets are deliberately separate from
settings: they never ride a settings export or a profile bundle.
Core test suite grows by 21 cases (895 → 916); everything above is
**Unverified** on a real machine until SEAN's on-machine pass (see
RELEASE_CHECKLIST).

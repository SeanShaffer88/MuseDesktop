# What's new

The release notes MuseDesktop shows on the first launch of a new version (S9 #82). This file is generated from CHANGELOG.md's current-version section at release time (see RELEASE_CHECKLIST.md) — do not edit it by hand; a test pins it to the changelog so the two can't drift.

## [2.1.0] — 2026-11-02

### Added

- **Snippet templates with fill-in blanks.** A prompt snippet's
  text can now contain blanks written as `{{name}}` — for example
  `Explain {{topic}} to a {{audience}}`. When you insert a snippet
  that has blanks (from Ctrl+K or the ⋯ menu), a small dialog asks
  for each one first, and the finished text lands in the message
  box, still unsent, exactly like a plain snippet. The same blank
  used twice is asked once and filled in everywhere it appears;
  cancelling the dialog inserts nothing. Snippets without blanks
  insert exactly as before.

- **Draft meter + tidy paste.** The status bar now shows the
  current draft's rough size as you write — words and an estimated
  token count, with a plain note when a draft gets long for one
  message. Pasting something long into the message box brings a
  quiet offer in the status bar: **Tidy it** replaces the draft
  with the same text under a one-line "Pasted text (N words):"
  header, quoted as a blockquote, so a wall of pasted text reads
  as material you're sending rather than as your message; **Leave
  it** keeps the paste exactly as it was. The paste itself is
  never blocked or altered, the offer is never repeated for an
  already-tidied draft, and only the paste's length ever leaves
  the page — its text reaches the app through the same draft
  report drafts autosave already uses. Settings → Account
  profiles gains a switch for the meter and the offer; it is
  independent of the drafts autosave switch.

- **Follow-up chips.** When an answer runs long — 150 words or
  more — a small row of one-click follow-ups appears above the
  message box once the answer has finished: **Summarize**,
  **Give an example**, **Turn into steps**, **Make a table**,
  **Pros and cons**, **Explain simpler**. Choosing one puts that
  request in the message box, still unsent, ready to edit or
  send. The row is offered once per answer and goes away when
  you send, move to another conversation, or start writing a
  reply of your own. The answer is only ever measured: its word
  count is counted on the page itself and never leaves it — the
  only thing the app hears is which chip was clicked.
  Settings → Account profiles gains a switch for the chips,
  beside the draft meter's; it is on by default and independent
  of the meter and drafts switches.

- **Ask about this page.** When a tab is showing a page you
  opened — an article you followed from a conversation, say —
  the ⋯ menu's **Ask about this page** (or Ctrl+K) starts a new
  conversation with that page's readable text already in the
  message box, labelled with the page's title and where it came
  from, and the box focused ready for your question. Nothing is
  sent; the question is yours to type. Navigation and page
  furniture are stripped from the text, and a very long page is
  cut down to fit — the text itself says so when that happens.
  The command steps aside on a conversation (that's what export
  is for), and the page's text is read only at your click: it is
  never logged and never stored — it goes into that one message
  box and nowhere else.

- **Screenshot → ask.** The ⋯ menu's **Screenshot to
  conversation…** (or Ctrl+K) dims the screen and lets you drag a
  rectangle anywhere — across monitors, mixed display scales
  included — and releasing the mouse starts a new conversation
  with that piece of the screen attached as a picture and the
  starter line "What is happening in this screenshot?" already in
  the message box, ready to edit or send. Nothing is saved and
  there is no attach button to hunt for: the picture goes in
  through the page's own upload control, and on a page whose
  shape won't take it that way, the picture is put on the
  clipboard instead and the status bar tells you to paste it with
  Ctrl+V. Esc or a right-click cancels, and the captured image
  exists as a file only between the grab and the attach — it is
  deleted immediately afterwards, whatever happened.

- **Voice in, voice out.** The ⋯ menu (or Ctrl+K) gains the
  app's own two voice paths — separate from quick dictate, which
  still hands off to Muse's own voice button as always.
  **Dictate into the message box** listens with Windows' speech
  recognition and types what you say into the message box,
  unsent, like a snippet you spoke; choose it again to stop.
  It uses a recognizer for the app's language when Windows has
  one, and the status bar says so plainly when it has to use
  another language's recognizer, when no recognizer is
  installed, or when no microphone can be found. **Read the
  last answer aloud** reads the newest answer with Windows'
  text-to-speech: headings and paragraphs become natural
  pauses, code blocks are announced ("Code block.") rather
  than read out, and the status bar holds Pause/Resume, Stop,
  and a speed button (0.75×–2×, applying from the next
  paragraph). Both stop when you switch tabs or conversations,
  lock the app, or close the window. Everything happens on this
  PC: dictation uses Windows' on-device recognition — your
  voice is never recorded and never leaves the machine — and
  the answer is read from the page only at your click, spoken,
  and forgotten, never logged or stored.

- **Context packs.** The ⋯ menu's **Context packs…** (or
  Ctrl+K) opens a window where you can save a bundle of local
  material under one name: files from this PC, notes you write
  into the pack, and snippets from your library (added by
  picking, typing, or dropping files onto the window).
  **Start conversation with this pack** opens a new conversation
  with the whole bundle in the message box as one organized
  block — a heading naming the pack, then a labelled section
  per item — still unsent, ready for your question. Files are
  read as text only at that moment: each file can contribute
  up to 8,000 characters and a whole pack up to 24,000, the
  block itself says when something was cut down, and a file
  that has moved, can't be read, or isn't text becomes a
  one-line note in its section instead of stopping the rest.
  A snippet that's no longer in the library gets the same
  honest placeholder. Headings name files by their file name
  only — never their folders — and pack contents are never
  logged. Deleting a pack asks first and can be undone until
  you close the window; the files and snippets themselves are
  never touched.

- **Standing briefs.** A brief is a long-form card for a project
  or topic — **Background**, **Rules**, **Preferences** — kept on
  this PC in its own file, so Muse can start every session
  already knowing the situation. Briefs live in the new
  **Library** window (Ctrl+K: "New conversation with a brief…"),
  which gathers everything you keep into one place: the snippet
  library and the reading list get tabs of their own there —
  second doors onto the same stores, never copies — and the
  **Briefs** tab holds the cards, with an editor for the name and
  the three sections (each section can be left empty, but a brief
  needs at least one section with words in it). **New conversation
  with this brief** opens a fresh conversation with the brief
  composed as one message in the message box — still unsent,
  always: you read it and press Send yourself, and that send is
  what makes it the conversation's first message. A brief's words
  are never logged or sent anywhere else.

- **Quick-attach strip.** A slim strip now sits above the
  message box holding your recent files — the newest downloads
  and screenshots (from Downloads and Pictures\Screenshots),
  plus a **Pasted image** chip whenever the clipboard holds a
  picture. Click a file to attach it to the conversation you're
  in, or drag it onto the page; either way it goes in through
  the page's own upload control, and if the page's shape won't
  take it that way, the file is put on the clipboard instead
  and the status bar tells you to paste it with Ctrl+V. An
  **Add files…** button at the strip's end attaches any other
  file the same way. The strip keeps itself current as files
  arrive (no polling), shows only file types the page's upload
  takes, and reads names and times only — never a file's
  contents. Its ✕ hides it; the ⋯ menu's **Quick-attach strip**
  item (or Settings → Account profiles) brings it back. It
  appears in the Standard and Left Rail layouts; Compact Focus,
  whose point is an uncluttered page, leaves it hidden until
  you switch layouts.

- **Compare mode.** The ⋯ menu's **Compare mode…** (or Ctrl+K)
  opens a separate window holding two Muse panes side by
  side, each signed in as a profile you pick — when you have
  two profiles, the panes start on different ones. Both panes
  open on a fresh conversation, and the window's one message
  box sends the same prompt to both at once: type the question,
  press **Send to both** (or Enter), and read the two answers
  next to each other. Each pane reports its own result under
  its profile picker — "Sent", "Couldn't find the message
  box", or "Still loading — try again" — and the prompt stays
  in the box unless both panes sent, so a half-sent question
  is never lost. This is the one place the app sends for you,
  and only because the button you pressed is the send itself.
  The panes are plain pages for now: drafts, follow-up chips,
  and the other conversation tools don't run inside them, and
  the prompt and the answers are never logged or stored.


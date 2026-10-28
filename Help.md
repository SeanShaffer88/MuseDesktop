# Help & troubleshooting

Plain-language fixes for the problems people actually hit with MuseDesktop. Everything on these pages works offline — the only thing that needs the internet is reporting a problem on GitHub (the link at the bottom of the Help window, which opens in your browser).

## Muse keeps asking me to sign in

What you see: Muse asks you to sign in again every time you open MuseDesktop, or signs you out in the middle of using it.

Why it happens: your sign-in lives in a cookie stored in your profile's data folder on this PC. If that cookie is cleared, expires, or can't be saved, Muse has no way to know it's you — so it asks again. Muse itself also signs sessions out from time to time; that part is normal and not something MuseDesktop controls.

Try this:
1. Make sure you're signing in on the profile you expect — different profiles (Settings → Account profiles) have separate sign-ins and don't share cookies.
2. Sign in once more, then quit MuseDesktop completely (tray → Exit) and start it again. A clean quit is when the session cookie gets saved.
3. If it keeps happening, clear this profile's site data and sign in fresh: use the button below to open Settings → Privacy & security, choose "All site data" for "This profile only", and Clear. Your chats are in your Muse account, not on this PC, so nothing is lost.
4. Check that nothing else is wiping the data: a very aggressive "PC cleaner" that empties app data folders will sign you out of everything, MuseDesktop included.

## Voice dictation doesn't hear me

What you see: Muse's voice/dictation button does nothing, shows an error, or the words you say never appear.

Why it happens: dictation needs three things to agree — a working microphone in Windows, Windows' permission for apps to use it, and MuseDesktop's permission (the app grants the microphone to muse.ai automatically, and refuses every other site). If any one of the three says no, voice input fails quietly.

Try this:
1. Run the built-in check: the button below opens Settings → Privacy & security — choose "Test microphone…", allow a moment, and say something. The result tells you plainly whether the microphone was granted and whether sound actually arrived.
2. If the test says the request was denied, open Windows Settings → Privacy & security → Microphone and turn on both the master switch and "Let desktop apps access your microphone".
3. If the test says no sound was picked up, check Windows Settings → Sound → Input: the right microphone selected, not muted, and its volume up. Headsets with a hardware mute switch catch everyone once.
4. If the test passes but Muse still can't hear you, reload the page (tray → Reload, or the toolbar button) — a page that loaded before the microphone was fixed can hold on to the failure.

## I'm not getting notifications

What you see: Muse finishes a task (or a download completes) and nothing tells you — no pop-up, no sound, no tray change.

Why it happens: there are two notification systems. Muse's own alerts depend on muse.ai deciding to send one and on Windows allowing it. MuseDesktop's own balloons (for example, "download finished") additionally respect Windows Do Not Disturb and the quiet-hours schedule you can set in Settings — and when they keep quiet, the log says why.

Try this:
1. Send yourself one: in Settings → Privacy & security, choose "Send test notification" (it's in the "Microphone & notification tests" group). The result line tells you if it was sent, and whether quiet hours or Windows Do Not Disturb is currently holding MuseDesktop's automatic notifications back (a test you ask for is sent anyway).
2. Check Windows Settings → Notifications: MuseDesktop (for its balloons) and your browser-style notifications from muse.ai must be allowed, and Focus Assist / Do Not Disturb schedules can hold everything until later.
3. The button below opens Settings → Tray & notifications: check quiet hours isn't covering the whole day by accident, and that "Tell me when a download finishes while the window is hidden" is on if it's download alerts you're missing.
4. Remember the app can't manufacture Muse alerts: if a task finishes without muse.ai sending a notification, there's nothing for Windows to show.
5. Approval cards specifically: when Muse shows an approval card (a permission prompt) while the MuseDesktop window isn't the one you're looking at, the app beeps once and flashes its taskbar button — Settings → Tray & notifications → "Beep when an approval needs my attention" (on by default). It only notices the card; the approving click is always yours. If the beep stops working after a Muse page update, the card's buttons may have changed shape — that's a known limit, not your settings.

## The WebView2 Runtime is missing or too old

What you see: MuseDesktop says the WebView2 Runtime is missing, shows a blank window, or Diagnostics says the runtime is far out of date.

Why it happens: MuseDesktop draws Muse using Microsoft's WebView2 Runtime — the same engine Edge uses — instead of bundling a whole browser. It's already on Windows 11 and nearly all Windows 10 PCs, and Microsoft updates it with Edge, but a minority of PCs don't have it (or have a very old one).

Try this:
1. If MuseDesktop showed the "runtime missing" message, use its button — it opens Microsoft's own installer page for the free Evergreen WebView2 Runtime. Install it (no restart needed) and start MuseDesktop again.
2. If the runtime is merely old, let Edge update: open Edge → Settings → About, or run Windows Update — the runtime updates itself alongside.
3. The button below opens Diagnostics, which shows the exact runtime version found on this PC and whether it's missing, old, or healthy — the first thing worth checking before anything else.
4. Don't copy a "fixed version" runtime folder next to MuseDesktop.exe to work around this: MuseDesktop uses the Evergreen runtime on purpose (so security fixes arrive from Microsoft), and a fixed folder is ignored — Diagnostics will point it out if one's there.

## Downloads fail or never finish

What you see: a file Muse made starts downloading and then fails, stalls at a percentage, or vanishes without landing in your folder.

Why it happens: usually the connection dropped mid-download, the folder it was headed for isn't writable (a removed USB drive, a network folder that's offline), or the disk filled up. The Downloads window records failures with a reason whenever the web engine reports one.

Try this:
1. Open the Downloads window (⋯ menu → Downloads…): failed downloads from THIS session can be retried with the Retry button. Retry re-downloads into the folder your settings name now, so fix the folder first (next step) if that was the problem.
2. The button below opens Settings → Downloads: check the downloads folder points somewhere that exists and has space, and — if a Save-As dialog kept cancelling things — whether "Ask where to save each download" is on by accident.
3. Reconnect and retry: most "network failed" downloads succeed on a second try once Wi-Fi is back. If a download stalls at the same percentage every time, ask Muse to produce the file again — a fresh link restarts the transfer cleanly.
4. If downloads from one profile fail but another works, that profile may have its own downloads-folder override (Settings → Account profiles) pointing somewhere unusable.

## Organizing downloads: sorting, archiving, notes, and checking a file

What you see: your downloads folder is an unsorted pile, you want proof a downloaded file is the genuine article, or you keep downloading the same file twice.

Why it happens: MuseDesktop used to stop at "the file landed". The Downloads window (⋯ menu → Downloads…) now carries the filing and checking tools, and Settings → Downloads carries their switches.

Try this:
1. **Sort…** files finished downloads into subfolders by type (Images, Documents, Archives, Audio, Video, Programs, Other) or by your own rules from Settings → Downloads. You see the whole plan before anything moves, nothing is ever overwritten, and **Undo sort** puts everything back. **Archive…** does the same into folders dated by the day each file finished.
2. **Note…** attaches a short note to a history entry — "the signed copy", "for the tax folder". Notes stay on this PC, with the history.
3. **Verify SHA-256…** checks a file against a hash its sender published: paste the hash and MuseDesktop compares it on this PC. The entry then shows "SHA-256 verified" or "SHA-256 check FAILED". A FAILED file is not the file the hash describes — don't run it; download it again from the sender's own page.
4. If MuseDesktop asks "already downloaded?" before saving, that's the duplicate guard: the same name and size finished earlier and is still in your downloads. Yes keeps both copies (the new one under a fresh name); No skips it. Turn it off in Settings → Downloads if you re-download on purpose all the time.
5. A download that fails on a network or server error retries itself a few times with growing waits — the status bar announces each one. Disk-full and permission failures never retry (they would fail identically): fix the cause, then use Retry in the Downloads window.
6. To download a whole list at once, use **Add links…** and paste one link per line: MuseDesktop drops the lines that aren't links, shows you the list, and runs at most 4 at a time.

## I forgot my app password

What you see: MuseDesktop opens to the lock screen and the app password or PIN you set isn't accepted — or you've plain forgotten it.

Why it happens: the app lock guards the open app on this PC. Only a salted one-way hash of your password is stored — the password itself is never written anywhere, so nobody (including MuseDesktop) can show it to you or recover it. What DOES exist is a tested way to remove the lock: it also resets your settings to defaults, but it does not sign you out, because sign-ins live in separate profile folders, not in the settings file.

Try this:
1. First, retry slowly — after 5 wrong tries in a row the lock pauses for about 30 seconds, which can look like a correct password being refused.
2. To remove the lock: quit MuseDesktop completely (close the lock screen, and if a tray icon remains, right-click it → Exit).
3. Open the data folder by hand: press Windows+R, type %APPDATA%\MuseDesktop and press Enter. (In portable mode the folder is the "data" folder beside MuseDesktop.exe.) Once you're back in, the button below opens Settings → Data, which has an "Open data folder" button for next time.
4. Delete the file settings.json in that folder. Don't touch anything else — especially not the WebView2 profile folders under %LOCALAPPDATA%\MuseDesktop, which hold your sign-ins.
5. Start MuseDesktop: no lock, default settings, and your default profile is still signed in. Re-apply any settings you cared about (exporting them first is the habit that makes this painless next time), and set a new app password in Settings → Privacy & security if you still want one.

The same applies to a per-profile PIN (Settings → Account profiles): each profile can ask for its own PIN when MuseDesktop launches with it, on top of the app password — the app password is always asked first. Profile PINs are stored the same one-way way, so a forgotten profile PIN has the same cure: the settings.json removal above clears every profile PIN along with the app password, and no profile is signed out by it. Inside an unlocked app you can also remove a profile's PIN in Settings, but that asks for the current PIN first.

## The page won't load

What you see: a blank page, a spinner that never ends, or the failure screen saying you're offline, the name couldn't be found, or the connection failed — maybe right after the PC woke from sleep.

Why it happens: Muse is a website, so MuseDesktop needs the internet to show it. A dropped connection, waking from sleep before Wi-Fi is back, a DNS hiccup, or (rarely) muse.ai itself being down all look similar from inside the app. When your connection comes back, MuseDesktop retries the failed page once on its own.

Try this:
1. Wait a few seconds after waking the PC or reconnecting — Wi-Fi often needs a moment, and the automatic one-time retry fires when the connection returns.
2. Press the toolbar Reload button. If the failure screen is showing, its Retry button does the same thing.
3. Check another site in your regular browser: if nothing loads anywhere, it's the connection, not the app. If only Muse fails, it may be muse.ai itself — waiting is the fix.
4. If a proxy is involved (work/school networks), the button below opens Diagnostics, which shows the proxy Windows is configured with — MuseDesktop never overrides it; WebView2 follows Windows.
5. Still stuck every launch? Start MuseDesktop with --safe-mode (it opens at the home page and skips the update check), or use the guided reset in Settings → Data — it backs your settings up before changing anything.

## Updating to a new version

What you see: the status bar (or the ⋯ menu) says a new version of MuseDesktop is available, and the update window offers Download, then Install.

Why it happens: MuseDesktop checks GitHub Releases for newer versions (Settings → Behavior controls whether it checks, and how often). Nothing downloads and nothing installs until you accept — an update is always your decision, never a surprise.

Try this:
1. Choose Download: the installer is downloaded with progress shown, and its SHA-256 fingerprint is checked against the fingerprint published with the release before it can run. If the two don't match, the file is deleted and nothing runs.
2. Choose Install: from there, everything is automatic. You don't need to close MuseDesktop first — the installer closes it for you (even if it's hidden in the tray), installs the new version over the old one, and reopens MuseDesktop when it's done. Your sign-in and settings carry over.
3. Rather wait? The same window can snooze the reminder until tomorrow or next week — MuseDesktop won't keep asking in between.
4. Changed your mind afterwards? Diagnostics can reinstall the previous version, when that version arrived through the in-app updater. That older installer still uses the classic setup wizard: you'll click through it yourself, and it doesn't reopen MuseDesktop for you.

## Commands & the command palette

What you see: you want to get to a setting, a download, another window, or a help page without clicking through menus — or you middle-clicked the tray icon and a small search box appeared.

Why it happens: the command palette is MuseDesktop's one keyboard-first way to reach everything the shell can do. Press Ctrl+K in the app (or middle-click the tray icon for the compact version), start typing — a few letters are enough, they don't have to be the start of the words — and press Enter. The actions you use most are pinned at the top as "Recent" the next time you open it. Recent only ever remembers which built-in actions you ran; it never stores page names, file names, or anything from your chats.

Try this:
1. Switching windows: the palette's "Switch window…" command (and the ⋯ menu item of the same name) lists your open windows most-recently-used first, with each window's profile color dot and page title — arrow keys and Enter jump straight there.
2. Switching profiles from the keyboard: Ctrl+Shift+1 through Ctrl+Shift+9 switch to the 1st–9th profile in your profile list. These work inside MuseDesktop only; there is no Windows-wide version, on purpose — Ctrl+Shift+number means something in many other apps, and MuseDesktop won't take those keys away from them everywhere.
3. Moving windows: type "tile" or "monitor" in the palette for the per-window actions — tile left/right, move to the next monitor, always-on-top, and (for the main window) focus mode.
4. The full list: this page ends with every command the palette offers, generated from the palette itself when you open the page — so it can never describe a command that isn't really there. Your profiles, recent downloads, and open windows appear in the palette too; they're yours, so they're not printed here.
5. Finding a setting by eye: the Settings window lists its topics down the left side (General, Appearance, Profiles, Workspaces, Notifications, Focus, Downloads, Data, Language, Accessibility, Privacy). Pick a topic to see just that topic's settings, grouped in cards — or type in the search box at the top of the list, and Settings jumps to the topic that holds the match. Settings reopens where you left it, on the same topic.

## Focus mode, timers, reading list & notes

What you see: you want to concentrate on one conversation, keep a page for later, jot something down without leaving the app, or see a plain summary of your week.

Why it happens: MuseDesktop's focus & productivity tools (Settings → Focus & productivity) are all local — they count minutes and keep lists on this PC only, and nothing about them is ever sent anywhere.

Try this:
1. Focus mode (F8, or the ⋯ menu) fills the screen with the main window and hides the app's own bars; Esc brings them back. If you turn on the focus timer in Settings, a block counts down while you work, and when it ends a gentle tray reminder suggests a break — it's a reminder, not a lock: dismiss it and carry on whenever you like. The separate session timer runs Pomodoro-style work/break cycles (25/5 by default) with its countdown in the status bar and the tray tooltip.
2. Reading list: ⋯ menu → "Save page to reading list" keeps the current Muse page (its title and in-app path only — never the full address) under Reading list…, and Ctrl+K finds saved pages too. Mark them read as you finish them.
3. Quick note: ⋯ menu → "Quick note…" (or Ctrl+K) files a note under the profile you're using; "Open notes" shows the notebook. If you want it from any app, turn on the quick-note hotkey in Settings (Ctrl+Alt+N by default) — it's off until you ask for it.
4. Scheduled commands run a command-palette action at startup, daily at a time, or once — but only while MuseDesktop is running; nothing is registered with Windows. The weekly digest and the stats dashboard (⋯ menu) summarize the week from the counts kept on this PC, and the dashboard's Clear button deletes those counts.

## Bookmarks, drafts, snippets & reading comfort

What you see: you want to mark one message in a conversation and come back to it later, pick up a half-typed message where you left off, reuse a prompt you type often, export a conversation to a file, or just make the conversation easier to read.

Why it happens: these are MuseDesktop's conversation tools. Everything about them is stored on this PC only — bookmarks, drafts, and snippets live in small files beside your settings, and nothing is ever sent anywhere. A few of them work by adding small touches to the Muse page itself (a wider scrollbar, a bookmark button on each message); if Muse's page ever changes shape, those touches quietly stop appearing rather than getting in the page's way.

Try this:
1. Bookmarks: hover a message in a conversation and click its bookmark button. You'll be asked for a nickname first — a bookmark can't be saved without one, because the nickname is how you'll recognize it later. The Bookmarks button at the window's upper right — just left of the zoom controls (or Ctrl+K) opens your list; choosing one opens that conversation and highlights the message. Bookmarks can only be revisited from inside MuseDesktop — that's deliberate: they hold a snippet of the message to find it by, and they never leave the app. If a message has been edited since, you'll land in the conversation and be told the exact text couldn't be found.
2. Drafts: what you type in the message box is kept — per conversation, for the profile you're using — and put back when you return to that conversation, even after restarting the app. Sending the message (or emptying the box) clears its draft. Turn this off in Settings → Account profiles if you'd rather nothing was kept.
3. Snippets: save the prompts you reuse in Settings → Account profiles (a name and the text), then insert one from Ctrl+K ("Insert snippet: …") or the ⋯ menu's Snippets list. The text lands in the message box for you to edit before sending — nothing is ever sent for you.
4. Export: ⋯ menu → "Export conversation as Markdown…" saves the chat as a tidy text file (your messages and Muse's, code blocks intact) wherever you choose. "Export conversation as PDF…" prints the conversation page to a PDF as it appears on screen.
5. Reading comfort: Settings → Account profiles holds, per profile, the always-visible conversation scrollbar (on by default), the chat text size, the reading-column width, and the line spacing. Changes apply when you press OK in Settings.
6. Token ring: the small ring at the upper right, between Bookmarks and the zoom controls, shows how full the current conversation's context is. If the Muse page itself displays a usage count, the ring reads that; if not, it estimates from the conversation's length — estimates wear a "~" and say so. Click the ring for the numbers, the source, and a Refresh button. The limit the estimate is measured against lives in Settings → Appearance & toolbar ("Estimated context limit") — set it to your plan's context window if that differs from 200,000.

## Backdrop, title bar tint, custom accent, theme schedule & mini mode

Why it happens: nothing is broken — these are MuseDesktop's appearance extras (Settings → Appearance & toolbar, plus two items in the main window's ⋯ menu), and this page explains what each one does and where its limits are. They restyle MuseDesktop's own windows only; the Muse page is never changed by any of them, and Windows High Contrast turns the decorative ones off.

Try this:
1. Backdrop (Mica / Acrylic): on Windows 11, Settings → Appearance → "Window backdrop" can put the window on Mica (a soft tint of your wallpaper) or Acrylic (a stronger frosted blur). If nothing changes, check three things: Windows 11 is required (Windows 10 keeps the solid window, by design), Windows' own transparency effects must be on (Windows Settings → Personalization → Colors), and High Contrast must be off.
2. Title bar tint: the title bar wears the active profile's color so you can see which profile a window belongs to. Turn it off in Settings → Appearance if you prefer the plain bar. It follows the profile color — change the color on the profile in Settings → Account profiles.
3. Custom accent: tick "Custom accent color" and type any color as six hex digits (for example E50914). The app adjusts the color until it passes the same readability check as the built-in accents, on both themes — so a pale yellow, say, gets deepened rather than washing out. Unticking returns to the preset accent.
4. Theme schedule: choose "Follow schedule" as the shell theme and set "Light from" and "Dark from" (24-hour times). The shell then switches between light and dark by the clock, checked every minute. The dark time may be earlier than the light time — the light hours then wrap past midnight, which suits night owls.
5. Mini mode: ⋯ menu → "Mini mode" shrinks the app to a small always-on-top strip showing the profile color, the page's icon and title, and your downloads with Pause all / Resume all. "Expand" on the strip (or opening the window from the tray) brings the full window back. The page is not inside the strip — it stays in the main window, which hides while the strip is up.
6. Tray icon styles and window opacity: Settings → Appearance sets the tray icon's painting (color, monochrome, or profile-tinted) and how its attention count shows (numbered badge, plain dot, or none). The ⋯ menu's "Window opacity" slider makes the window see-through down to 60%; it is per-window and for this session only, it does nothing in High Contrast, and the window goes fully solid whenever the app locks.

## Languages, language packs, announcements & text spacing

Why it happens: nothing is broken — these are MuseDesktop's language and accessibility extras (Settings → Language & announcements), and this page explains what each one does and where its limits are. They change MuseDesktop's own windows only; the Muse page itself is never translated or restyled by any of them.

Try this:
1. App language: pick "System default" to follow the Windows display language, or choose a language yourself — Spanish (Español) ships with the app. A change applies to windows opened afterwards, and to everything the next time the app starts. If a string hasn't been translated yet, it shows in English — Diagnostics lists, per language, exactly how many strings that is.
2. Language packs: anyone can translate the shell. "Export a blank language pack…" saves a template holding every string in English; translate each one, then "Import a language pack…" brings the finished file in. A pack is checked before it's used: it must translate every string, and any {0}-style placeholders must match the English exactly — if something's off, the import is refused and the reason is shown in plain words. Imported packs live in the app-data folder's language-packs folder and can be removed by deleting their file there.
3. Spoken announcements: MuseDesktop can speak events through a screen reader (a download finishing, an update appearing, a timer ending, focus mode turning on). Turn any kind off here if it's noise; every announcement — spoken or not — is still recorded under Diagnostics → Recent announcements, so nothing is lost.
4. Text spacing: Standard / Relaxed / Wide widens the space between lines of text in the app's own windows, for low-vision reading. (Windows' text controls don't offer letter spacing, so the control is line spacing — the spacing that matters most.) The Muse page has its own reading controls in Settings → Account profiles.
5. Keyboard order: every window's controls follow a deliberate tab order — the order you'd read the screen — and every control has a spoken name. If you find one that doesn't, that's a bug worth reporting.

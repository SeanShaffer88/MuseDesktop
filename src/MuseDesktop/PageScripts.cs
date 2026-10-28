using System.Text.Json;

namespace MuseDesktop;

/// <summary>
/// The page bridge (R5B #201–#206): the one script MuseDesktop
/// installs into the hosted Muse page, plus the builders for the
/// calls the shell makes into it. This is a scoped, user-requested
/// exception to the standing "never touch the page" rule (see
/// DECISIONS.md, v1.6.0): the script only RESTYLES at the user's
/// settings (a style element the shell feeds), OBSERVES at the user's
/// settings (composer text for drafts, a click on the bookmark
/// button the user asked for), and RUNS the user's own commands
/// (find this bookmarked message, insert this snippet, read this
/// conversation for export). It never sends anything, never clicks
/// anything, and never navigates.
///
/// Everything is defensive by contract: each feature is wrapped so a
/// failure kills only that feature, the message-candidate selectors
/// are structural (testids / main / form — never build-artifact class
/// hashes), and when the page's shape isn't what's expected the
/// features quietly no-op. The shell treats every value coming back
/// as untrusted (Core's PageBridge / ConversationExport parsers).
///
/// The bridge protocol (page → shell, posted as objects):
///   {type:"md-ready"}                 — installed; shell pushes style + saved draft
///   {type:"md-bookmark", text}        — bookmark button clicked on a message
///   {type:"md-draft", text}           — composer text changed (debounced)
///   {type:"md-draft-sent"}            — a send was attempted (Enter / send button)
///   {type:"md-approval-appeared", signal} — an approval card appeared (R7C; presence + which watcher rule matched, never any card content)
///   {type:"md-approval-gone"}         — that card went away (R7C)
/// </summary>
public static class PageScripts
{
    /// <summary>The document-created script: installed once per web view; every later document in that view gets it again automatically.</summary>
    public const string DocumentScript = """
        (function () {
          'use strict';
          if (window.__mdInstalled) return;
          window.__mdInstalled = true;

          var post = function (msg) {
            try { window.chrome.webview.postMessage(msg); } catch (e) { }
          };
          var collapse = function (s) { return (s || '').replace(/\s+/g, ' ').trim(); };

          // ── style application (#201/#205): the shell owns the CSS
          // text (built by Core from the profile); the page just
          // hosts the one element it lands in.
          window.__mdApplyStyle = function (css) {
            try {
              var el = document.getElementById('musedesktop-page-style');
              if (!css) { if (el) el.remove(); return; }
              if (!el) {
                el = document.createElement('style');
                el.id = 'musedesktop-page-style';
                (document.head || document.documentElement).appendChild(el);
              }
              el.textContent = css;
            } catch (e) { }
          };

          // ── message candidates: structural selectors only. The
          // conversation's messages, best-known shape first.
          function messageElements() {
            var found = document.querySelectorAll('[data-testid*="message"]');
            if (found.length) return found;
            found = document.querySelectorAll('main article');
            if (found.length) return found;
            return document.querySelectorAll('main [class*="message"]');
          }

          // ── bookmark buttons (#202): one per message, top-right,
          // appearing on hover. A click reports the message's text
          // (collapsed, capped) — the shell asks for the nickname.
          function ensureBookmarkButtons() {
            try {
              var msgs = messageElements();
              for (var i = 0; i < msgs.length; i++) {
                var el = msgs[i];
                if (el.getAttribute('data-md-bookmarked')) continue;
                el.setAttribute('data-md-bookmarked', '1');
                if (getComputedStyle(el).position === 'static') el.style.position = 'relative';
                var btn = document.createElement('button');
                btn.type = 'button';
                btn.className = 'md-bookmark-btn';
                btn.textContent = '\uD83D\uDD16';
                btn.title = 'Bookmark this message in MuseDesktop';
                btn.setAttribute('aria-label', 'Bookmark this message in MuseDesktop');
                btn.style.cssText = 'position:absolute;top:4px;right:4px;z-index:30;width:26px;height:26px;' +
                  'border:none;border-radius:6px;background:rgba(127,127,127,0.18);cursor:pointer;' +
                  'font-size:13px;line-height:26px;text-align:center;opacity:0.45;padding:0;';
                btn.addEventListener('mouseenter', function () { this.style.opacity = '1'; });
                btn.addEventListener('mouseleave', function () { this.style.opacity = '0.45'; });
                (function (host) {
                  btn.addEventListener('click', function (ev) {
                    ev.stopPropagation();
                    ev.preventDefault();
                    var text = collapse(host.innerText || host.textContent || '').slice(0, 400);
                    post({ type: 'md-bookmark', text: text });
                  });
                })(el);
                el.appendChild(btn);
              }
            } catch (e) { }
          }

          // ── find + highlight (#202 jump): locate the snippet in
          // the page's text, scroll to it, outline its block briefly.
          window.__mdFindAndHighlight = function (snippet) {
            try {
              var target = collapse(snippet);
              if (!target) return false;
              // Try the whole snippet, then shorter heads — an edited
              // message may still share its opening.
              var tries = [target];
              if (target.length > 160) tries.push(target.slice(0, 160));
              if (target.length > 90) tries.push(target.slice(0, 90));
              if (target.length > 50) tries.push(target.slice(0, 50));
              var walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
              var node;
              while ((node = walker.nextNode())) {
                var hay = collapse(node.nodeValue || '');
                if (!hay) continue;
                for (var t = 0; t < tries.length; t++) {
                  if (hay.indexOf(tries[t]) >= 0) {
                    var el = node.parentElement;
                    if (!el) return false;
                    el.scrollIntoView({ block: 'center', behavior: 'smooth' });
                    var oldOutline = el.style.outline, oldBg = el.style.backgroundColor;
                    el.style.outline = '2px solid #E50914';
                    el.style.backgroundColor = 'rgba(229, 9, 20, 0.10)';
                    setTimeout(function () {
                      el.style.outline = oldOutline;
                      el.style.backgroundColor = oldBg;
                    }, 2600);
                    return true;
                  }
                }
              }
              return false;
            } catch (e) { return false; }
          };

          // ── conversation extraction (#203): messages in page
          // order, role guessed from the element's own markers.
          window.__mdExtractConversation = function () {
            try {
              var msgs = messageElements();
              var out = [];
              for (var i = 0; i < msgs.length; i++) {
                var el = msgs[i];
                var marker = ((el.getAttribute('data-testid') || '') + ' ' + (el.className || '')).toLowerCase();
                var role = '';
                if (marker.indexOf('user') >= 0 || marker.indexOf('human') >= 0) role = 'user';
                else if (marker.indexOf('assistant') >= 0 || marker.indexOf('Muse') >= 0) role = 'assistant';
                // The bookmark button rides inside the message — its
                // glyph is not conversation text.
                var clone = el.cloneNode(true);
                var btns = clone.querySelectorAll('.md-bookmark-btn');
                for (var b = 0; b < btns.length; b++) btns[b].remove();
                var text = (clone.innerText || clone.textContent || '').trim();
                if (text) out.push({ role: role, text: text });
              }
              return JSON.stringify(out);
            } catch (e) { return null; }
          };

          // ── usage probe (R7B): read-only, like everything else
          // here. Two things: the usage figure the PAGE ITSELF
          // displays (if it displays one — a "12,400 / 200,000
          // tokens" style label or a context percent, found via
          // aria-labels, titles, or short visible texts that talk
          // about context/tokens), and the conversation's total
          // text LENGTH in characters — the only input the shell's
          // fallback estimate needs. The text itself never leaves
          // the page for this; counts and a length only.
          window.__mdUsageProbe = function () {
            try {
              var out = { used: null, limit: null, percent: null, textLength: 0 };
              var msgs = messageElements();
              var total = 0;
              for (var i = 0; i < msgs.length; i++) {
                var t = msgs[i].innerText || msgs[i].textContent || '';
                total += t.length;
              }
              out.textLength = total;
              var pairRe = /(?:context|tokens?|usage)[^0-9]{0,40}([0-9][0-9,]*)\s*(?:\/|of)\s*([0-9][0-9,]*)/i;
              var pctRe = /(?:context|tokens?|usage)[^0-9%]{0,40}([0-9]{1,3}(?:\.[0-9]+)?)\s*%/i;
              var texts = [];
              var labelled = document.querySelectorAll('[aria-label], [title]');
              for (var a = 0; a < labelled.length && texts.length < 400; a++) {
                var s = (labelled[a].getAttribute('aria-label') || '') + ' ' + (labelled[a].getAttribute('title') || '');
                if (s.trim()) texts.push(s);
              }
              var all = document.querySelectorAll('main *');
              for (var b = 0; b < all.length && texts.length < 900; b++) {
                var el = all[b];
                if (el.children.length) continue;      // leaf elements only
                var own = (el.textContent || '').trim();
                if (own && own.length <= 120) texts.push(own);
              }
              var parseNum = function (s) { return parseInt(String(s).replace(/,/g, ''), 10); };
              for (var c = 0; c < texts.length; c++) {
                var m = pairRe.exec(texts[c]);
                if (m) {
                  var used = parseNum(m[1]), limit = parseNum(m[2]);
                  if (isFinite(used) && isFinite(limit) && limit > 0) {
                    out.used = used; out.limit = limit;
                    break;
                  }
                }
              }
              if (out.used === null) {
                for (var d = 0; d < texts.length; d++) {
                  var pm = pctRe.exec(texts[d]);
                  if (pm) {
                    var pct = parseFloat(pm[1]);
                    if (isFinite(pct)) { out.percent = pct; break; }
                  }
                }
              }
              return JSON.stringify(out);
            } catch (e) { return null; }
          };

          // ── the composer: drafts (#204) + snippet insert (#206).
          function findComposer() {
            var candidates = document.querySelectorAll(
              'form textarea, form [contenteditable="true"], textarea, [contenteditable="true"][role="textbox"], div[contenteditable="true"]');
            var visible = [];
            for (var i = 0; i < candidates.length; i++) {
              var c = candidates[i];
              if (c.offsetParent !== null || c === document.activeElement) visible.push(c);
            }
            return visible.length ? visible[visible.length - 1] : null;
          }
          function composerText(el) {
            if (!el) return '';
            if (typeof el.value === 'string') return el.value;
            return el.innerText || el.textContent || '';
          }
          function setComposerText(el, text) {
            if (typeof el.value === 'string') {
              var setter = Object.getOwnPropertyDescriptor(window.HTMLTextAreaElement.prototype, 'value').set;
              setter.call(el, text);
            } else {
              el.focus();
              var sel = window.getSelection();
              sel.selectAllChildren(el);
              document.execCommand('insertText', false, text);
            }
            el.dispatchEvent(new Event('input', { bubbles: true }));
          }

          var draftTimer = null;
          var boundComposer = null;
          window.__mdPendingDraft = null;
          function bindComposer() {
            try {
              var el = findComposer();
              if (!el || el === boundComposer) return;
              boundComposer = el;
              if (window.__mdPendingDraft) {
                var pending = window.__mdPendingDraft;
                window.__mdPendingDraft = null;
                if (!collapse(composerText(el))) setComposerText(el, pending);
              }
              el.addEventListener('input', function () {
                if (draftTimer) clearTimeout(draftTimer);
                draftTimer = setTimeout(function () {
                  post({ type: 'md-draft', text: composerText(el) });
                }, 700);
              });
              el.addEventListener('keydown', function (ev) {
                if (ev.key === 'Enter' && !ev.shiftKey && !ev.ctrlKey && !ev.altKey && !ev.metaKey) {
                  post({ type: 'md-draft-sent' });
                }
              });
            } catch (e) { }
          }
          window.__mdRestoreDraft = function (text) {
            try {
              var el = findComposer();
              if (el) {
                if (!collapse(composerText(el))) { setComposerText(el, text); return true; }
                return false;
              }
              window.__mdPendingDraft = text;
              return true;
            } catch (e) { return false; }
          };
          window.__mdInsertSnippet = function (text) {
            try {
              var el = findComposer();
              if (!el) return false;
              el.focus();
              if (typeof el.value === 'string') {
                var current = el.value;
                var sep = current && !/\s$/.test(current) ? ' ' : '';
                setComposerText(el, current + sep + text);
              } else {
                document.execCommand('insertText', false, text);
                el.dispatchEvent(new Event('input', { bubbles: true }));
              }
              return true;
            } catch (e) { return false; }
          };
          // A click on a send-looking control near the composer also
          // means "sent" (capture phase: the page may consume it).
          document.addEventListener('click', function (ev) {
            try {
              var t = ev.target;
              var btn = t && t.closest ? t.closest('button') : null;
              if (!btn) return;
              var label = ((btn.getAttribute('aria-label') || '') + ' ' + (btn.textContent || '')).toLowerCase();
              if (label.indexOf('send') >= 0 && boundComposer) post({ type: 'md-draft-sent' });
            } catch (e) { }
          }, true);

          // ── approval watcher (R7C; detection rebuilt R7C2):
          // OBSERVE ONLY. It reports that an approval card
          // appeared / went away so the shell can beep for the
          // user's attention; it never clicks, focuses, or
          // otherwise touches the card, and it reports presence
          // plus WHICH rule matched — no card text ever crosses
          // the bridge.
          //
          // Two signals; either one is enough:
          //  1. 'allow-prefix' (PRIMARY, R7C2): a visible button
          //     whose whole label STARTS with "allow" or "always
          //     allow" (case-insensitive, whole-word start, so
          //     "allowance" doesn't count). This is the rule from
          //     the user's own browser alert script, which works
          //     on the real page; R7C's stricter detector below
          //     never once fired there (the field failure behind
          //     R7C2 — see DECISIONS). The label is capped at 60
          //     characters so a paragraph wearing a button
          //     costume can't match.
          //  2. 'pair' (the R7C rule, kept as the back-up): a
          //     visible container holding BOTH an approve-style
          //     action and a deny-style one, where an action is a
          //     real <button> or role="button" element whose
          //     ENTIRE label is exactly one of the known words.
          //     Chat text can quote "allow" and "deny" all day —
          //     a message contains no such button pair, so it
          //     can't trip this. The reported container is the
          //     smallest element holding both actions; the page
          //     root, <main> itself, a message element, and
          //     anything containing the composer are never the
          //     card. (R7C also required the container to be
          //     under 60% of the viewport; R7C2 dropped that —
          //     one more way for a real card to be filtered out.)
          var APPROVE_WORDS = ['allow', 'allow once', 'allow always', 'approve', 'yes'];
          var DENY_WORDS = ['deny', 'reject', 'cancel', 'no'];
          var ALLOW_LABEL_MAX = 60;
          var APPROVAL_SETTLE_MS = 300;
          function isShown(el) {
            if (!el) return false;
            var r = el.getBoundingClientRect();
            if (!r.width || !r.height) return false;
            if (el.offsetParent === null && getComputedStyle(el).position !== 'fixed') return false;
            return true;
          }
          function buttonLabel(el) {
            return collapse(el.getAttribute('aria-label') || el.innerText || el.textContent || '');
          }
          function actionWord(el) {
            var label = buttonLabel(el).toLowerCase();
            if (!label || label.length > 24) return '';
            if (APPROVE_WORDS.indexOf(label) >= 0) return 'approve';
            if (DENY_WORDS.indexOf(label) >= 0) return 'deny';
            return '';
          }
          function allowPrefixHit(el) {
            var label = buttonLabel(el).toLowerCase();
            if (!label || label.length > ALLOW_LABEL_MAX) return false;
            return /^allow\b/.test(label) || /^always allow\b/.test(label);
          }
          // Returns 'allow-prefix', 'pair', or null.
          function detectApprovalSignal() {
            try {
              var buttons = document.querySelectorAll('button, [role="button"]');
              var approves = [], denies = [];
              for (var i = 0; i < buttons.length; i++) {
                if (!isShown(buttons[i])) continue;
                if (allowPrefixHit(buttons[i])) return 'allow-prefix';
                var w = actionWord(buttons[i]);
                if (w === 'approve') approves.push(buttons[i]);
                else if (w === 'deny') denies.push(buttons[i]);
              }
              if (!approves.length || !denies.length) return null;
              // Smallest common container of an approve/deny pair.
              var pair = null;
              outer:
              for (var a = 0; a < approves.length; a++) {
                for (var d = 0; d < denies.length; d++) {
                  var node = approves[a];
                  while (node && node !== document.body) {
                    if (node.contains(denies[d])) { pair = node; break outer; }
                    node = node.parentElement;
                  }
                }
              }
              if (!pair || pair === document.body || pair === document.documentElement) return null;
              var main = document.querySelector('main');
              if (main && pair === main) return null;
              var testid = (pair.getAttribute('data-testid') || '').toLowerCase();
              if (testid.indexOf('message') >= 0) return null;      // a message is not a card
              var composer = findComposer();
              if (composer && pair.contains(composer)) return null; // the composer block is not a card
              if (!isShown(pair)) return null;
              return 'pair';
            } catch (e) { return null; }
          }
          // Transitions post only after the new state survives a
          // short settle delay (~0.3s, R7C2 — it was ~0.8s in
          // R7C), so a framework re-render that drops and re-adds
          // the node doesn't flap the signal — and the shell
          // still hears about a card that appears on an otherwise
          // static page (the confirm timer). The verdict that
          // settles is re-checked at post time, so the reported
          // signal is the one that actually survived.
          var approvalReported = false;
          var approvalCandidateSeen = false;
          var approvalCandidateSignal = null;
          var approvalCandidateSince = 0;
          var approvalConfirmTimer = null;
          function scanApproval() {
            try {
              var signal = detectApprovalSignal();
              var present = !!signal;
              if (present === approvalReported) {
                approvalCandidateSeen = false;
                approvalCandidateSignal = null;
                return;
              }
              var now = Date.now();
              if (!approvalCandidateSeen || approvalCandidateSignal !== signal) {
                approvalCandidateSeen = true;
                approvalCandidateSignal = signal;
                approvalCandidateSince = now;
                if (!approvalConfirmTimer) {
                  approvalConfirmTimer = setTimeout(function () {
                    approvalConfirmTimer = null;
                    scanApproval();
                  }, APPROVAL_SETTLE_MS + 50);
                }
                return;
              }
              if (now - approvalCandidateSince < APPROVAL_SETTLE_MS) return;
              approvalReported = present;
              approvalCandidateSeen = false;
              approvalCandidateSignal = null;
              if (present) post({ type: 'md-approval-appeared', signal: signal });
              else post({ type: 'md-approval-gone' });
            } catch (e) { }
          }

          // ── watch the page: conversations render and grow as the
          // user chats, so buttons/composer binding re-check on
          // mutation (debounced) rather than once.
          var scanTimer = null;
          function scheduleScan() {
            if (scanTimer) return;
            scanTimer = setTimeout(function () {
              scanTimer = null;
              ensureBookmarkButtons();
              bindComposer();
              scanApproval();
            }, 600);
          }
          try {
            new MutationObserver(scheduleScan).observe(document.documentElement,
              { childList: true, subtree: true });
          } catch (e) { }
          ensureBookmarkButtons();
          bindComposer();
          scanApproval();
          post({ type: 'md-ready' });
        })();
        """;

    /// <summary>Feed the profile's CSS (Core's <see cref="PageStyleCss"/>) to the page — replaces whatever the last apply set.</summary>
    public static string ApplyStyle(string css) =>
        $"window.__mdApplyStyle && window.__mdApplyStyle({JsonSerializer.Serialize(css)});";

    /// <summary>Ask the page to find + highlight a bookmarked message's snippet; the script result is the JSON literal true/false.</summary>
    public static string FindSnippet(string snippet) =>
        $"(window.__mdFindAndHighlight ? window.__mdFindAndHighlight({JsonSerializer.Serialize(snippet)}) : false)";

    /// <summary>Ask the page for the conversation's messages as a JSON string (the script result is that string, itself JSON-encoded).</summary>
    public const string ExtractConversation =
        "(window.__mdExtractConversation ? window.__mdExtractConversation() : null)";

    /// <summary>Ask the page for its usage probe (R7B): the usage figure the page itself displays, if any, plus the conversation's text length for the shell's fallback estimate — as a JSON string (the script result is that string, itself JSON-encoded). Observe-only: the probe reads labels and counts characters; it changes nothing.</summary>
    public const string UsageProbe =
        "(window.__mdUsageProbe ? window.__mdUsageProbe() : null)";

    /// <summary>Offer a saved draft back to the page's composer (it lands only when the composer is empty and present, or when it appears).</summary>
    public static string RestoreDraft(string text) =>
        $"(window.__mdRestoreDraft ? window.__mdRestoreDraft({JsonSerializer.Serialize(text)}) : false)";

    /// <summary>Insert a snippet's text into the composer (never sends); the script result is the JSON literal true/false.</summary>
    public static string InsertSnippet(string text) =>
        $"(window.__mdInsertSnippet ? window.__mdInsertSnippet({JsonSerializer.Serialize(text)}) : false)";
}

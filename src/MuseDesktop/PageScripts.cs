using System.Text.Json;

namespace MuseDesktop;

/// <summary>
/// The page bridge (R5B #201–#206): the one script MuseDesktop
/// installs into the hosted Muse page, plus the builders for the
/// calls the shell makes into it. This is a scoped, user-requested
/// exception to the standing "never touch the page" rule (see
/// DECISIONS.md, v1.6.0): the script only RESTYLES at the user's
/// settings (a style element the shell feeds), adds a presentation-only
/// fold control to long code blocks, OBSERVES at the user's settings
/// (composer text for drafts, a click on the bookmark button the user
/// asked for), and RUNS the user's own commands
/// (find this bookmarked message, insert this snippet, read this
/// conversation for export). It never sends anything, clicks Muse
/// controls on the user's behalf, or navigates.
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
                    var clone = host.cloneNode(true);
                    var controls = clone.querySelectorAll('.md-bookmark-btn, .md-code-fold-btn');
                    for (var i = 0; i < controls.length; i++) controls[i].remove();
                    var text = collapse(clone.innerText || clone.textContent || '').slice(0, 400);
                    post({ type: 'md-bookmark', text: text });
                  });
                })(el);
                el.appendChild(btn);
              }
            } catch (e) { }
          }

          // ── code-block folding: presentation only. The page text is
          // untouched; a quiet-period heuristic waits for the answer to
          // settle, and visible stop/busy markers postpone auto-folding.
          function ensureCodeFoldStyles() {
            if (document.getElementById('musedesktop-code-fold-style')) return;
            var style = document.createElement('style');
            style.id = 'musedesktop-code-fold-style';
            style.textContent = 'pre[data-md-code-folded="1"] {' +
              'max-height:14em !important;overflow-y:hidden !important;}';
            (document.head || document.documentElement).appendChild(style);
          }
          function codeBlockDetails(pre) {
            var text = pre.innerText || pre.textContent || '';
            return { text: text, lines: text.split(/\r?\n/).length };
          }
          function isLongCodeBlock(pre) {
            var details = codeBlockDetails(pre);
            return details.lines > 14 || details.text.length > 1000;
          }
          function setCodeFoldState(pre, button, folded) {
            var details = codeBlockDetails(pre);
            pre.setAttribute('data-md-code-folded', folded ? '1' : '0');
            button.setAttribute('aria-expanded', folded ? 'false' : 'true');
            button.textContent = folded
              ? 'Expand code (' + details.lines + ' lines)'
              : 'Collapse code';
          }
          function ensureCodeFoldButtons() {
            try {
              ensureCodeFoldStyles();
              var msgs = messageElements();
              for (var i = 0; i < msgs.length; i++) {
                var blocks = msgs[i].querySelectorAll('pre');
                for (var j = 0; j < blocks.length; j++) {
                  var pre = blocks[j];
                  if (!isLongCodeBlock(pre)) continue;
                  var button = pre.nextElementSibling;
                  if (!button || !button.classList.contains('md-code-fold-btn')) {
                    button = document.createElement('button');
                    button.type = 'button';
                    button.className = 'md-code-fold-btn';
                    button.style.cssText = 'display:block;margin:6px 0 10px auto;padding:4px 9px;' +
                      'border:1px solid rgba(127,127,127,0.35);border-radius:6px;' +
                      'background:rgba(127,127,127,0.12);color:inherit;cursor:pointer;' +
                      'font:inherit;font-size:0.85em;';
                    (function (code, control) {
                      control.addEventListener('click', function (ev) {
                        ev.preventDefault();
                        ev.stopPropagation();
                        code.setAttribute('data-md-code-user-choice', '1');
                        setCodeFoldState(code, control,
                          code.getAttribute('data-md-code-folded') !== '1');
                      });
                    })(pre, button);
                    pre.parentNode.insertBefore(button, pre.nextSibling);
                  }
                  if (!button.hasAttribute('aria-expanded'))
                    setCodeFoldState(pre, button, pre.getAttribute('data-md-code-folded') === '1');
                }
              }
            } catch (e) { }
          }
          function autoFoldLongCodeBlocks() {
            try {
              var msgs = messageElements();
              for (var i = 0; i < msgs.length; i++) {
                var blocks = msgs[i].querySelectorAll('pre');
                for (var j = 0; j < blocks.length; j++) {
                  var pre = blocks[j];
                  if (!isLongCodeBlock(pre) || pre.getAttribute('data-md-code-user-choice') === '1') continue;
                  var button = pre.nextElementSibling;
                  if (button && button.classList.contains('md-code-fold-btn'))
                    setCodeFoldState(pre, button, true);
                }
              }
            } catch (e) { }
          }
          function generationIsActive() {
            try {
              if (document.querySelector('[aria-busy="true"], [data-state="streaming"], [data-status="streaming"]'))
                return true;
              var controls = document.querySelectorAll('button, [role="button"]');
              for (var i = 0; i < controls.length; i++) {
                var label = ((controls[i].getAttribute('aria-label') || '') + ' ' +
                  (controls[i].getAttribute('data-testid') || '') + ' ' +
                  (controls[i].textContent || '')).toLowerCase().trim();
                if (/^(stop|stop generating|stop response|stop generation|cancel generation)\b/.test(label))
                  return true;
              }
            } catch (e) { }
            return false;
          }
          var foldTimer = null;
          function scheduleAutoFold() {
            if (foldTimer) clearTimeout(foldTimer);
            foldTimer = setTimeout(function () {
              foldTimer = null;
              if (!generationIsActive()) autoFoldLongCodeBlocks();
            }, 1800);
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
                var btns = clone.querySelectorAll('.md-bookmark-btn, .md-code-fold-btn');
                for (var b = 0; b < btns.length; b++) btns[b].remove();
                var text = (clone.innerText || clone.textContent || '').trim();
                if (text) out.push({ role: role, text: text });
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

          // ── watch the page: conversations render and grow as the
          // user chats, so buttons/composer binding re-check on
          // mutation (debounced) rather than once.
          var scanTimer = null;
          function scheduleScan() {
            scheduleAutoFold();
            if (scanTimer) return;
            scanTimer = setTimeout(function () {
              scanTimer = null;
              ensureBookmarkButtons();
              ensureCodeFoldButtons();
              bindComposer();
            }, 600);
          }
          try {
            new MutationObserver(scheduleScan).observe(document.documentElement,
              { childList: true, subtree: true, characterData: true, attributes: true,
                attributeFilter: ['aria-busy', 'data-state', 'data-status'] });
          } catch (e) { }
          ensureBookmarkButtons();
          ensureCodeFoldButtons();
          bindComposer();
          scheduleAutoFold();
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

    /// <summary>Offer a saved draft back to the page's composer (it lands only when the composer is empty and present, or when it appears).</summary>
    public static string RestoreDraft(string text) =>
        $"(window.__mdRestoreDraft ? window.__mdRestoreDraft({JsonSerializer.Serialize(text)}) : false)";

    /// <summary>Insert a snippet's text into the composer (never sends); the script result is the JSON literal true/false.</summary>
    public static string InsertSnippet(string text) =>
        $"(window.__mdInsertSnippet ? window.__mdInsertSnippet({JsonSerializer.Serialize(text)}) : false)";
}

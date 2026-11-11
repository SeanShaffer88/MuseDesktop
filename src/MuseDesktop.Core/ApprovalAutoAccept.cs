namespace MuseDesktop.Core;

/// <summary>Separate opt-in action policy; the existing attention watcher remains observe-only.</summary>
public static class ApprovalAutoAccept
{
    public static bool CanRun(bool enabled, bool unlocked, bool visible, string? source) =>
        enabled && unlocked && visible && DualMode.RestorableRoute(source) is not null;

    // No observers, page timers, message bridge, storage or content returned.
    // Native code must authorize EVERY invocation. The first matching scan only
    // stages a candidate; another scan after 1.5 seconds must see the same card.
    public const string ResetPendingScript = """
        (() => {
          try {
            const u = new URL(location.href);
            if (u.origin !== 'https://muse.ai' || u.username || u.password) return 'paused';
            const s = window.__mdOneTimeApproval;
            if (s) s.pending = null;
          } catch (_) {}
          return 'paused';
        })()
        """;

    public const string Script = """
        (() => {
          try {
            const u = new URL(location.href);
            if (u.origin !== 'https://muse.ai' || u.username || u.password ||
                !(u.pathname === '/' || /^\/chat\//.test(u.pathname)) ||
                document.visibilityState !== 'visible') return 'blocked';
            const key = '__mdOneTimeApproval';
            const s = window[key] || (window[key] = {
              seen: new WeakSet(), cards: new WeakSet(), handled: new Set(), pending: null, route: u.pathname });
            if (s.route !== u.pathname) {
              s.pending = null; // Old DOM actions remain handled even during an SPA route transition.
              s.handled = new Set(); s.route = u.pathname;
            }
            const normalize = v => String(v || '').replace(/\s+/g, ' ').trim().toLowerCase();
            // Non-cryptographic, page-local duplicate suppression. Collisions
            // only suppress a click; no content or digest crosses the bridge.
            const digest = text => {
              let h = 2166136261;
              for (let i = 0; i < text.length; i++) h = Math.imul(h ^ text.charCodeAt(i), 16777619);
              return (h >>> 0).toString(16);
            };
            const label = e => normalize(e.getAttribute('aria-label') || e.innerText || e.textContent);
            const shown = e => {
              if (!e || !e.isConnected || e.hidden || e.closest('[inert], [aria-hidden="true"]')) return false;
              const r = e.getBoundingClientRect();
              if (!(r.width > 0 && r.height > 0)) return false;
              for (let p = e; p; p = p.parentElement) {
                const style = getComputedStyle(p);
                if (p.hidden || style.display === 'none' || style.visibility === 'hidden' ||
                    style.visibility === 'collapse' || Number(style.opacity || 1) <= 0) return false;
              }
              return true;
            };
            const enabled = e => shown(e) && !e.disabled &&
              !e.closest('[aria-disabled="true"]') && getComputedStyle(e).pointerEvents !== 'none';
            const permanent = t => /\b(always|remember|never ask|permanent|siempre|recordar|no volver)\b/.test(t);
            const once = new Set(['allow once', 'allow this time', 'approve once', 'approve this request',
              'approve', 'accept once', 'permitir una vez', 'permitir esta vez', 'aprobar una vez', 'aprobar']);
            const no = new Set(['deny', 'reject', 'cancel', 'no', 'denegar', 'rechazar', 'cancelar']);
            const permanentChoices = new Set(['always allow', 'allow always', 'permitir siempre', 'siempre permitir']);
            const quoted = e => !!e.closest('pre, code, blockquote, template, [contenteditable="true"], [data-testid="message-text"], [data-testid="markdown"]');
            const nodes = document.querySelectorAll('button, [role="button"]');
            if (nodes.length > 1000) return 'unrecognised';
            const actions = [...nodes].filter(enabled);
            const candidates = [];
            for (const button of actions) {
              const text = label(button);
              if (text.length > 60 || permanent(text) || quoted(button) ||
                  !(once.has(text) || text === 'allow' || text === 'permitir')) continue;
              let card = button.parentElement;
              for (let depth = 0; card && depth < 7; depth++, card = card.parentElement) {
                if (card === document.body || card === document.documentElement || card.tagName === 'MAIN') break;
                const marker = normalize(card.getAttribute('data-testid'));
                if (marker.includes('message') && !marker.includes('approval')) break;
                const buttons = [...card.querySelectorAll('button, [role="button"]')].filter(enabled);
                if (!buttons.some(b => no.has(label(b)))) continue;
                const title = card.querySelector('h1, h2, h3, h4, [role="heading"]');
                const identified = marker.includes('approval') || card.hasAttribute('data-approval-card') ||
                  (title && /\b(approval|approve|aprobación|aprobar)\b/.test(label(title)));
                if (!identified) continue;
                if (!shown(card) || card.textContent.length > 10000 || quoted(card) ||
                    card.querySelector('input:not([type="hidden"]), textarea, select, [contenteditable="true"]')) break;
                // Bare Allow is unambiguous only beside a separate permanent choice.
                const hasPermanent = buttons.some(b => permanentChoices.has(label(b)));
                if ((text === 'allow' || text === 'permitir') && !hasPermanent) break;
                const positives = buttons.filter(b => once.has(label(b)) ||
                  ((label(b) === 'allow' || label(b) === 'permitir') && hasPermanent));
                if (positives.length !== 1 || positives[0] !== button) break;
                // Private, bounded fingerprint stays in this document only.
                const fingerprint = card.textContent + '\n' + text;
                const identity = card.getAttribute('data-request-id') || card.getAttribute('data-approval-id');
                candidates.push({ button, card, fingerprint, id: digest(identity || fingerprint) });
                break;
              }
            }
            if (candidates.length !== 1) {
              s.pending = null;
              return candidates.length ? 'ambiguous' : 'none';
            }
            const c = candidates[0];
            if (s.seen.has(c.button) || s.cards.has(c.card) || s.handled.has(c.id)) {
              s.pending = null; return 'duplicate';
            }
            const now = performance.now();
            if (!s.pending || s.pending.button !== c.button || s.pending.card !== c.card ||
                s.pending.fingerprint !== c.fingerprint || now < s.pending.at) {
              s.pending = { ...c, at: now };
              return 'pending';
            }
            if (now - s.pending.at < 1500) return 'pending';
            const r = c.button.getBoundingClientRect(), x = r.x + r.width / 2, y = r.y + r.height / 2;
            const hit = document.elementFromPoint(x, y);
            if (!enabled(c.button) || !shown(c.card) || !c.card.contains(c.button) ||
                x < 0 || y < 0 || x >= window.innerWidth || y >= window.innerHeight ||
                !(hit === c.button || c.button.contains(hit)) ||
                document.visibilityState !== 'visible') { s.pending = null; return 'blocked'; }
            s.pending = null;
            s.seen.add(c.button); // Mark BEFORE dispatch; never retry the same DOM action.
            s.cards.add(c.card);
            if (s.handled.size >= 128) s.handled.delete(s.handled.values().next().value);
            s.handled.add(c.id);
            c.button.click();
            return 'clicked'; // Dispatch only, not proof of server-side success.
          } catch (_) { return 'unrecognised'; }
        })()
        """;
}

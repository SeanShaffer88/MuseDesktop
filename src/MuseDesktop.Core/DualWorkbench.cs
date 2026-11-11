using System.Text.Json;

namespace MuseDesktop.Core;

/// <summary>A named pair stores only user-chosen names, profile IDs, safe routes and zoom.</summary>
public sealed class DualPair
{
    public string Name { get; set; } = "";
    public string LeftProfileId { get; set; } = Profiles.DefaultId;
    public string RightProfileId { get; set; } = Profiles.DefaultId;
    public string LeftRoute { get; set; } = "/";
    public string RightRoute { get; set; } = "/";
    public double LeftZoom { get; set; } = 1;
    public double RightZoom { get; set; } = 1;

    public void Sanitize()
    {
        Name = DualWorkbench.CleanName(Name);
        LeftProfileId = Profiles.IsValidId(LeftProfileId) ? LeftProfileId : Profiles.DefaultId;
        RightProfileId = Profiles.IsValidId(RightProfileId) ? RightProfileId : Profiles.DefaultId;
        LeftRoute = DualSession.CleanRoute(LeftRoute);
        RightRoute = DualSession.CleanRoute(RightRoute);
        LeftZoom = DualSession.CleanZoom(LeftZoom);
        RightZoom = DualSession.CleanZoom(RightZoom);
    }
}

public static class DualWorkbench
{
    public const int MaxTransfer = 8000;
    public const int MaxNotes = 32000;
    public static string CleanName(string? value) =>
        new((value ?? "").Trim().Where(c => !char.IsControl(c)).Take(64).ToArray());
    public static bool CanInspect(string? url) => DualMode.RestorableRoute(url) is not null;
    public static double ScrollFraction(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;

    // Stable feature IDs, also used by the source-contract tests and release checklist.
    public static readonly IReadOnlyList<string> Features = new[]
    {
        "swap", "profiles", "pairs", "orientation", "expand", "detach", "focus",
        "shortcuts", "titles", "links", "transfer", "notes", "reply-indicators",
        "find", "scroll", "downloads", "recovery", "restore", "privacy", "accessibility",
    };
}

/// <summary>Only a recognized streaming-control transition counts, not guessed completion from idle text.</summary>
public sealed class DualReplyTracker
{
    private bool _wasStreaming;
    public bool Observe(bool streaming, bool background)
    {
        var completed = _wasStreaming && !streaming && background;
        _wasStreaming = streaming;
        return completed;
    }
    public void Reset() => _wasStreaming = false;
}

/// <summary>
/// Opt-in page tools. Never submit, click Send, collect a transcript,
/// install on OAuth pages, or persist selection/search/scroll data.
/// </summary>
public static class DualToolsScripts
{
    public const string Selection = """
        (() => String(window.getSelection?.() || '').slice(0, 8000))()
        """;

    public static string InsertSelection(string text)
    {
        var safe = JsonSerializer.Serialize(text.Length > DualWorkbench.MaxTransfer
            ? text[..DualWorkbench.MaxTransfer] : text);
        return """
            (() => {
              const text =
            """ + safe + """
            ;
              const boxes = [...new Set(document.querySelectorAll(
                'form textarea, textarea, [contenteditable="true"][role="textbox"], div[contenteditable="true"]'))]
                .filter(e => e.getClientRects().length && !e.disabled && !e.readOnly);
              if (boxes.length !== 1) return false;
              const el = boxes[0];
              el.focus();
              if (el.tagName === 'TEXTAREA') {
                const old = el.value || '';
                Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set.call(
                  el, old + (old ? '\n' : '') + text);
                el.dispatchEvent(new Event('input', {bubbles:true}));
              } else {
                const range = document.createRange();
                range.selectNodeContents(el); range.collapse(false);
                const selection = window.getSelection();
                selection.removeAllRanges(); selection.addRange(range);
                if (!document.execCommand('insertText', false, (el.innerText ? '\n' : '') + text)) return false;
                el.dispatchEvent(new Event('input', {bubbles:true}));
              }
              return true;
            })()
            """;
    }

    public const string Streaming = """
        (() => [...document.querySelectorAll(
          '[data-testid="stop-button"], button[aria-label="Stop generating"], button[aria-label="Stop response"], button[title="Stop generating"]')]
          .some(e => e.getClientRects().length > 0))()
        """;

    public const string StopScroll = "window.__mdDualStopSync?.();";
    public const string StartScroll = """
        (() => {
          window.__mdDualStopSync?.();
          let last = 0;
          const handler = e => {
            if (performance.now() < (window.__mdDualIgnoreScroll || 0) || performance.now() - last < 120) return;
            const el = e.target === document ? document.scrollingElement : e.target;
            if (!el || /TEXTAREA|INPUT/.test(el.tagName) || el.isContentEditable) return;
            const range = el.scrollHeight - el.clientHeight;
            if (range < 20) return;
            window.__mdDualScrollTarget = el;
            last = performance.now();
            window.chrome.webview.postMessage({type:'musedesktop-dual-scroll', ratio:el.scrollTop / range});
          };
          document.addEventListener('scroll', handler, true);
          window.__mdDualStopSync = () => document.removeEventListener('scroll', handler, true);
          return true;
        })()
        """;

    public static string ApplyScroll(double ratio) => """
        (() => {
          const candidates = [window.__mdDualScrollTarget, document.scrollingElement,
            ...document.querySelectorAll('main, main div, article')].filter(e =>
              e && !e.isContentEditable && e.scrollHeight - e.clientHeight > 20);
          const el = candidates[0];
          if (!el) return false;
          window.__mdDualIgnoreScroll = performance.now() + 300;
          el.scrollTop = (el.scrollHeight - el.clientHeight) *
        """ + ScrollNumber(ratio) + """
        ;
          return true;
        })()
        """;

    private static string ScrollNumber(double ratio) =>
        DualWorkbench.ScrollFraction(ratio).ToString(System.Globalization.CultureInfo.InvariantCulture);
}

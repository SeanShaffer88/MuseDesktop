namespace MuseDesktop.Core;

/// <summary>Where a URL is allowed to go.</summary>
public enum LinkDisposition
{
    /// <summary>Load inside the app's WebView.</summary>
    InApp,

    /// <summary>Hand to the user's default browser.</summary>
    ExternalBrowser,

    /// <summary>Refuse entirely (dangerous or unparsable).</summary>
    Block,
}

/// <summary>
/// Decides which URLs load inside the app, which go to the user's real
/// browser, and which are refused. Pure logic — no I/O — so it is fully
/// unit-tested.
///
/// Rules:
/// - Top-level navigation inside the WebView may go to any HTTPS page:
///   sign-in and connector auth flows redirect through several providers,
///   and Muse itself decides where its own pages link. Plain HTTP and
///   non-web schemes are refused at the top level.
/// - A <i>new-window</i> request (target=_blank / window.open) is stricter:
///   only Muse and known sign-in hosts stay in the app; every other
///   http(s) link opens in the user's default browser, where their real
///   tabs, extensions and history live.
/// </summary>
public static class UrlPolicy
{
    /// <summary>Home page of the app.</summary>
    public const string HomeUrl = "https://muse.ai/";

    /// <summary>
    /// Hosts whose pages belong inside the app even when opened as a new
    /// window: Muse itself plus the identity providers its sign-in uses.
    /// Matching is exact-host or true subdomain only — a look-alike such as
    /// <c>muse.ai.evil.com</c> or <c>evilmuse.ai</c> never matches.
    /// </summary>
    public static readonly IReadOnlyList<string> InAppHosts = new[]
    {
        "muse.ai",
        "meta.com",
        "facebook.com",
        "instagram.com",
        "accounts.google.com",
        "appleid.apple.com",
        "login.microsoftonline.com",
    };

    /// <summary>True when <paramref name="host"/> is, or is a subdomain of, an in-app host.</summary>
    public static bool IsInAppHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;
        host = host.Trim().TrimEnd('.');
        foreach (var allowed in InAppHosts)
        {
            if (host.Equals(allowed, StringComparison.OrdinalIgnoreCase))
                return true;
            if (host.EndsWith("." + allowed, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Policy for a top-level navigation of the main WebView.</summary>
    public static LinkDisposition ClassifyNavigation(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return LinkDisposition.Block;
        if (url.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
            return LinkDisposition.InApp;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return LinkDisposition.Block;

        return uri.Scheme switch
        {
            "https" => LinkDisposition.InApp,
            // Blob URLs are how web apps hand the browser a generated file
            // (Muse artifact downloads); WebView2 raises downloads for them.
            "blob" => LinkDisposition.InApp,
            _ => LinkDisposition.Block,
        };
    }

    /// <summary>Policy for a new-window request raised by page content.</summary>
    public static LinkDisposition ClassifyNewWindow(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return LinkDisposition.Block;
        if (url.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
            return LinkDisposition.InApp;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return LinkDisposition.Block;

        if (uri.Scheme is not ("https" or "http"))
            return LinkDisposition.Block;

        return IsInAppHost(uri.Host)
            ? LinkDisposition.InApp
            : LinkDisposition.ExternalBrowser;
    }

    /// <summary>
    /// True for permissions the app grants automatically, and only when the
    /// requesting origin is Muse itself: notifications (task-finished
    /// alerts) and the microphone (voice dictation). Everything else is
    /// refused rather than silently granted.
    /// </summary>
    public static bool IsAutoGrantedPermission(string? origin, string permissionKind)
    {
        if (permissionKind is not ("notifications" or "microphone"))
            return false;
        if (string.IsNullOrWhiteSpace(origin))
            return false;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            return false;
        return uri.Scheme == "https"
            && uri.Host.Equals("muse.ai", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A log-safe version of a URL: scheme, host and path only — never the
    /// query string or fragment, which can carry tokens or chat content.
    /// </summary>
    public static string RedactForLog(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "(none)";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "(unparsable)";
        return $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}";
    }
}

namespace MuseDesktop.Core;

/// <summary>One page in the navigation history model (v0.5.0, S1 #3).</summary>
public sealed class NavigationEntry
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
}

/// <summary>
/// A small, testable model of the pages the user has visited in this
/// session, layered over WebView2's own back/forward (which offers no
/// enumeration API). Successful top-level in-app navigations are
/// recorded; consecutive duplicates collapse; the list is capped.
/// The shell uses it for the Back/Forward history dropdowns (jump to an
/// entry = navigate to its URL) — WebView2 remains the source of truth
/// for CanGoBack/CanGoForward.
/// </summary>
public sealed class NavigationHistory
{
    public const int MaxEntries = 50;

    private readonly List<NavigationEntry> _entries = new();
    private int _index = -1;

    public IReadOnlyList<NavigationEntry> Entries => _entries;
    public int CurrentIndex => _index;
    public NavigationEntry? Current => _index >= 0 && _index < _entries.Count ? _entries[_index] : null;

    /// <summary>The page the user was on just before the current one (for Ctrl+Shift+T, S1 #9).</summary>
    public string? LastClosedPageUrl { get; private set; }

    /// <summary>Record a successful navigation to <paramref name="url"/>.</summary>
    public void Record(string url, string title = "")
    {
        if (string.IsNullOrWhiteSpace(url))
            return;
        if (Current is { } cur && string.Equals(cur.Url, url, StringComparison.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(title))
                cur.Title = title;
            return;
        }
        // Navigating away from the current page closes it (for restore).
        if (Current is { } prev)
            LastClosedPageUrl = prev.Url;

        // A fresh navigation drops any forward entries, like a browser.
        if (_index < _entries.Count - 1)
            _entries.RemoveRange(_index + 1, _entries.Count - _index - 1);
        _entries.Add(new NavigationEntry { Url = url, Title = title ?? "" });
        _index = _entries.Count - 1;
        while (_entries.Count > MaxEntries)
        {
            _entries.RemoveAt(0);
            _index--;
        }
    }

    /// <summary>Move the pointer back after a Back jump; returns the entry or null.</summary>
    public NavigationEntry? GoBack()
    {
        if (_index <= 0) return null;
        if (Current is { } prev) LastClosedPageUrl = prev.Url;
        _index--;
        return Current;
    }

    /// <summary>Move the pointer forward after a Forward jump; returns the entry or null.</summary>
    public NavigationEntry? GoForward()
    {
        if (_index >= _entries.Count - 1) return null;
        _index++;
        return Current;
    }

    /// <summary>Entries behind the current one, nearest first (Back dropdown).</summary>
    public IReadOnlyList<NavigationEntry> BackEntries()
    {
        var list = new List<NavigationEntry>();
        for (var i = _index - 1; i >= 0; i--) list.Add(_entries[i]);
        return list;
    }

    /// <summary>Entries ahead of the current one, nearest first (Forward dropdown).</summary>
    public IReadOnlyList<NavigationEntry> ForwardEntries()
    {
        var list = new List<NavigationEntry>();
        for (var i = _index + 1; i < _entries.Count; i++) list.Add(_entries[i]);
        return list;
    }

    /// <summary>Jump the pointer to an existing entry (history dropdown click).</summary>
    public NavigationEntry? JumpTo(NavigationEntry entry)
    {
        var i = _entries.IndexOf(entry);
        if (i < 0) return null;
        if (Current is { } prev && i != _index) LastClosedPageUrl = prev.Url;
        _index = i;
        return Current;
    }

    /// <summary>Mark a page as closed without navigating (crash overlay, S1 #9).</summary>
    public void NoteClosedPage(string? url)
    {
        if (!string.IsNullOrWhiteSpace(url))
            LastClosedPageUrl = url;
    }
}

/// <summary>Load-failure classification (v0.5.0, S1 #10).</summary>
public enum LoadFailureCategory
{
    Offline,
    HostUnreachable,
    DnsFailure,
    Certificate,
    Other,
}

/// <summary>
/// Maps WebView2 <c>CoreWebView2WebErrorStatus</c> names to a category and
/// a plain-language message. Core takes the status as a string so it stays
/// free of the WebView2 SDK; the shell passes <c>e.WebErrorStatus.ToString()</c>.
/// </summary>
public static class LoadFailureClassifier
{
    public static LoadFailureCategory Classify(string? webErrorStatus) => webErrorStatus switch
    {
        "Disconnected" or "CannotConnect" or "ConnectionAborted" or "ConnectionReset" => LoadFailureCategory.Offline,
        "ServerUnreachable" or "Timeout" => LoadFailureCategory.HostUnreachable,
        "HostNameNotResolved" => LoadFailureCategory.DnsFailure,
        "CertificateCommonNameIsIncorrect" or "CertificateExpired" or "CertificateRevoked"
            or "CertificateIsInvalid" or "ClientCertificateContainsErrors" => LoadFailureCategory.Certificate,
        _ => LoadFailureCategory.Other,
    };

    public static string Message(LoadFailureCategory category, string? statusName) => category switch
    {
        LoadFailureCategory.Offline =>
            "You appear to be offline. MuseDesktop will retry automatically when your connection comes back. Your sign-in and chats are stored by Muse in the cloud — nothing is lost.",
        LoadFailureCategory.HostUnreachable =>
            "Muse's servers couldn't be reached. This is usually a temporary network problem. Check your connection, then retry — MuseDesktop will also retry once automatically when your connection changes.",
        LoadFailureCategory.DnsFailure =>
            "This PC couldn't look up Muse's address (a DNS problem). Check your connection or network settings, then retry.",
        LoadFailureCategory.Certificate =>
            "The connection's security certificate wasn't valid, so MuseDesktop stopped the load to keep your sign-in safe. Check that this PC's date and time are correct, then retry.",
        _ =>
            $"The page failed to load ({statusName ?? "unknown error"}). Check your connection, then retry. Your sign-in and chats are stored by Muse in the cloud — nothing is lost.",
    };

    public static string MessageFor(string? webErrorStatus) => Message(Classify(webErrorStatus), webErrorStatus);
}

/// <summary>Window-title composition (v0.5.0, S1 #12).</summary>
public static class WindowTitle
{
    /// <summary>
    /// "&lt;page title&gt; — &lt;profile name&gt; — MuseDesktop" when more than
    /// one profile exists, else "&lt;page title&gt; — MuseDesktop". A blank page
    /// title falls back to "Muse".
    /// </summary>
    public static string Compose(string? pageTitle, string? profileName, bool multipleProfiles)
    {
        var page = string.IsNullOrWhiteSpace(pageTitle) ? "Muse" : pageTitle.Trim();
        return multipleProfiles && !string.IsNullOrWhiteSpace(profileName)
            ? $"{page} — {profileName.Trim()} — MuseDesktop"
            : $"{page} — MuseDesktop";
    }
}

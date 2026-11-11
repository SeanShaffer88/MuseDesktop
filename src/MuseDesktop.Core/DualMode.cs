namespace MuseDesktop.Core;

/// <summary>
/// Dual mode is two independent chats on the active profile, not a
/// broadcast composer. Opening it carries the current Muse page into
/// the left pane; the right starts at Home. No routes are persisted.
/// </summary>
public static class DualMode
{
    public static (string Left, string Right) InitialUrls(string? currentUrl)
    {
        // Only carry a Muse page into another view. In particular, do
        // not replay a third-party sign-in callback or an external page.
        var left = Uri.TryCreate(currentUrl, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && uri.Host.Equals("muse.ai", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(uri.UserInfo)
            && uri.IsDefaultPort
                ? uri.AbsoluteUri
                : UrlPolicy.HomeUrl;
        return (left, UrlPolicy.HomeUrl);
    }
}

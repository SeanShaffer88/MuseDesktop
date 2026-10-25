using System.IO;
using Microsoft.Web.WebView2.Core;
using MuseDesktop.Core;

namespace MuseDesktop;

/// <summary>One saved permission choice in the active profile (S7 #62), as the Permissions section shows it.</summary>
public sealed record PermissionRow(string Kind, string Origin, string Host, string State);

/// <summary>The outcome of a clear-data run (S7 #61): whether everything cleared, whether the running profile was touched, and the plain summary for the UI.</summary>
public sealed record ClearDataResult(bool Success, bool ActiveProfileTouched, string Summary);

/// <summary>
/// The bridge between the Settings window (which edits a settings
/// clone and must not touch WebView2 itself) and the live WebView2
/// profiles, for Slice 7's privacy features: granular clear-data
/// (#61), the permissions manager (#62), and the site-data viewer
/// (#63). Every operation targets the ACTIVE profile through the
/// running web view; other profiles are reached by standing up a
/// throwaway environment + controller over their data folder (the
/// folders are per-profile by design, and no other process holds
/// them). Read paths never look at cookie VALUES — names, counts and
/// domains only.
/// </summary>
public sealed class WebPrivacyGateway
{
    private readonly Func<CoreWebView2?> _liveWebView;
    private readonly Func<string> _activeProfileId;
    private readonly Func<IntPtr> _windowHandle;
    private readonly DiagnosticLog _log;
    private readonly Action<ClearDataChoice> _onActiveProfileCleared;

    public WebPrivacyGateway(
        Func<CoreWebView2?> liveWebView,
        Func<string> activeProfileId,
        Func<IntPtr> windowHandle,
        DiagnosticLog log,
        Action<ClearDataChoice> onActiveProfileCleared)
    {
        _liveWebView = liveWebView;
        _activeProfileId = activeProfileId;
        _windowHandle = windowHandle;
        _log = log;
        _onActiveProfileCleared = onActiveProfileCleared;
    }

    /// <summary>True when the running web view exists (clearing/inspecting the active profile needs it).</summary>
    public bool IsLiveReady => _liveWebView() is not null;

    /// <summary>
    /// The live web view's environment (the ACTIVE profile's), for the
    /// microphone test window (S10 #92): the test hosts its page in
    /// this environment so the request travels the real profile's
    /// permission machinery. Null until the web view exists.
    /// </summary>
    public CoreWebView2Environment? LiveEnvironment
    {
        get
        {
            try { return _liveWebView()?.Environment; }
            catch { return null; }
        }
    }

    /// <summary>The WebView2 runtime version for the connection dialog (#69), or null when unknown.</summary>
    public string? RuntimeVersion
    {
        get
        {
            try { return _liveWebView()?.Environment.BrowserVersionString; }
            catch { return null; }
        }
    }

    /// <summary>The choice → WebView2 kinds mapping. All = AllProfile, exactly the pre-v0.11.0 clear.</summary>
    public static CoreWebView2BrowsingDataKinds KindsFor(ClearDataChoice choice) => choice switch
    {
        ClearDataChoice.Cookies => CoreWebView2BrowsingDataKinds.Cookies,
        ClearDataChoice.Cache => CoreWebView2BrowsingDataKinds.DiskCache
                                 | CoreWebView2BrowsingDataKinds.CacheStorage,
        _ => CoreWebView2BrowsingDataKinds.AllProfile,
    };

    // ───────────────────────── #61 clear data ─────────────────────────

    /// <summary>
    /// Clear <paramref name="choice"/> from each named profile. The
    /// running profile goes through the live web view; the rest go
    /// through a temporary environment over their folder. A profile
    /// that fails is named in the summary — never silently skipped.
    /// </summary>
    public async Task<ClearDataResult> ClearAsync(
        ClearDataChoice choice, IReadOnlyList<(string Id, string Name)> profiles)
    {
        var kinds = KindsFor(choice);
        var cleared = new List<string>();
        var failed = new List<string>();
        var touchedActive = false;
        foreach (var (id, name) in profiles)
        {
            try
            {
                if (id == _activeProfileId())
                {
                    var live = _liveWebView()
                        ?? throw new InvalidOperationException("the web view isn't running yet");
                    await live.Profile.ClearBrowsingDataAsync(kinds);
                    touchedActive = true;
                }
                else
                {
                    await WithTemporaryProfileAsync(id, async profile =>
                        await profile.ClearBrowsingDataAsync(kinds));
                }
                cleared.Add(name);
            }
            catch (Exception ex)
            {
                _log.Warn($"Clear {choice} for profile {id} failed: {ex.Message}");
                failed.Add(name);
            }
        }
        if (touchedActive)
            _onActiveProfileCleared(choice);
        var summary = failed.Count == 0
            ? $"Cleared {ClearDataPlan.Title(choice).ToLowerInvariant()} for {string.Join(", ", cleared)}."
            : $"Cleared for {string.Join(", ", cleared)}; couldn't clear {string.Join(", ", failed)} — see the log.";
        _log.Info($"Clear data ({choice}): {summary}");
        return new ClearDataResult(failed.Count == 0, touchedActive, summary);
    }

    // ───────────────────────── #62 permissions ─────────────────────────

    /// <summary>The active profile's saved (non-default) permission choices, read live from WebView2.</summary>
    public async Task<IReadOnlyList<PermissionRow>> GetPermissionsAsync()
    {
        var live = _liveWebView()
            ?? throw new InvalidOperationException("the web view isn't running yet");
        var settings = await live.Profile.GetNonDefaultPermissionSettingsAsync();
        var rows = new List<PermissionRow>();
        foreach (var s in settings)
        {
            var host = Uri.TryCreate(s.PermissionOrigin, UriKind.Absolute, out var uri) ? uri.Host : s.PermissionOrigin;
            rows.Add(new PermissionRow(
                s.PermissionKind.ToString(), s.PermissionOrigin, host, s.PermissionState.ToString()));
        }
        return rows;
    }

    /// <summary>Set one saved permission choice in the active profile (Allow / Deny / Default = forget the choice).</summary>
    public async Task SetPermissionAsync(string kind, string origin, CoreWebView2PermissionState state)
    {
        var live = _liveWebView()
            ?? throw new InvalidOperationException("the web view isn't running yet");
        if (!Enum.TryParse<CoreWebView2PermissionKind>(kind, out var parsed))
            throw new InvalidOperationException($"unknown permission kind {kind}");
        await live.Profile.SetPermissionStateAsync(parsed, origin, state);
        _log.Info($"Permission {kind} for {UrlPolicy.RedactForLog(origin)} set to {state} from Settings");
    }

    // ───────────────────────── #63 site data ─────────────────────────

    /// <summary>
    /// Cookie facts for one profile: total count + per-domain counts.
    /// Cookie VALUES are never read into UI text — only Name/Domain
    /// metadata is touched, and only counts leave this method.
    /// Returns null when the profile can't be read right now.
    /// </summary>
    public async Task<(int Count, IReadOnlyList<(string Domain, int Count)> Domains)?> GetCookieSummaryAsync(string profileId)
    {
        try
        {
            if (profileId == _activeProfileId())
            {
                var live = _liveWebView();
                if (live is null)
                    return null;
                return Summarize(await live.Profile.CookieManager.GetCookiesAsync(null!));
            }
            (int Count, IReadOnlyList<(string Domain, int Count)> Domains)? result = null;
            await WithTemporaryProfileAsync(profileId, async profile =>
            {
                result = Summarize(await profile.CookieManager.GetCookiesAsync(null!));
            });
            return result;
        }
        catch (Exception ex)
        {
            _log.Warn($"Cookie summary for profile {profileId} failed: {ex.Message}");
            return null;
        }
    }

    private static (int Count, IReadOnlyList<(string Domain, int Count)> Domains) Summarize(
        IReadOnlyList<CoreWebView2Cookie> cookies)
    {
        var byDomain = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var cookie in cookies)
        {
            var domain = (cookie.Domain ?? "").Trim().TrimStart('.').ToLowerInvariant();
            if (domain.Length == 0)
                domain = "(no domain)";
            byDomain[domain] = byDomain.TryGetValue(domain, out var n) ? n + 1 : 1;
        }
        var domains = byDomain
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
        return (cookies.Count, domains);
    }

    /// <summary>
    /// Stand up a throwaway environment + controller over another
    /// profile's data folder, run <paramref name="use"/> against its
    /// default profile, and tear it down. The controller is created
    /// against the main window's handle and never shown.
    /// </summary>
    private async Task WithTemporaryProfileAsync(string profileId, Func<CoreWebView2Profile, Task> use)
    {
        var dir = AppPaths.ProfileDir(profileId);
        Directory.CreateDirectory(dir);
        var env = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null, userDataFolder: dir);
        var controller = await env.CreateCoreWebView2ControllerAsync(_windowHandle());
        try
        {
            await use(controller.CoreWebView2.Profile);
        }
        finally
        {
            controller.Close();
        }
    }
}

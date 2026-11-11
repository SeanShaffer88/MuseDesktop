using System.Text.Json;

namespace MuseDesktop.Core;

/// <summary>Only routes and reading preferences; never unsent text, titles, queries, or sign-in data.</summary>
public sealed class DualSession
{
    public string LeftRoute { get; set; } = "/";
    public string RightRoute { get; set; } = "/";
    public double LeftFraction { get; set; } = 0.5;
    public double LeftZoom { get; set; } = 1;
    public double RightZoom { get; set; } = 1;

    public void Sanitize()
    {
        LeftRoute = CleanRoute(LeftRoute);
        RightRoute = CleanRoute(RightRoute);
        LeftFraction = CleanFraction(LeftFraction);
        LeftZoom = CleanZoom(LeftZoom);
        RightZoom = CleanZoom(RightZoom);
    }

    private static string CleanRoute(string? route) =>
        route is not null && route.StartsWith('/') && !route.StartsWith("//", StringComparison.Ordinal)
            ? DualMode.RestorableRoute("https://muse.ai" + route) ?? "/" : "/";

    public static double CleanFraction(double fraction) =>
        double.IsFinite(fraction) ? Math.Clamp(fraction, 0.2, 0.8) : 0.5;

    public static double CleanZoom(double zoom) =>
        double.IsFinite(zoom) ? ZoomMath.Clamp(zoom, extendedRange: true) : 1;
}

/// <summary>Local-only Dual state per profile, separate from settings exports and account bundles.</summary>
public sealed class DualSessions
{
    public Dictionary<string, DualSession> ByProfile { get; set; } = new();

    public DualSession? Find(string profileId) =>
        ByProfile.TryGetValue(profileId, out var session) ? session : null;

    public DualSession ForProfile(string profileId)
    {
        if (!Profiles.IsValidId(profileId))
            throw new ArgumentException("Invalid profile id.", nameof(profileId));
        if (!ByProfile.TryGetValue(profileId, out var session))
            ByProfile[profileId] = session = new DualSession();
        return session;
    }

    /// <summary>False for failed/external/auth navigations and unchanged routes; the caller only saves changes.</summary>
    public bool RememberChat(string profileId, bool left, string? url)
    {
        if (!Profiles.IsValidId(profileId) || DualMode.RestorableRoute(url) is not { } route)
            return false;
        var session = ForProfile(profileId);
        if ((left ? session.LeftRoute : session.RightRoute) == route)
            return false;
        if (left) session.LeftRoute = route;
        else session.RightRoute = route;
        return true;
    }

    public void Sanitize()
    {
        ByProfile ??= new();
        foreach (var id in ByProfile.Keys.ToList())
        {
            if (!Profiles.IsValidId(id) || ByProfile[id] is null)
                ByProfile.Remove(id);
            else
                ByProfile[id].Sanitize();
        }
    }
}

public static class DualSessionStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static DualSessions Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new DualSessions();
            var sessions = JsonSerializer.Deserialize<DualSessions>(File.ReadAllText(path), Options)
                ?? new DualSessions();
            sessions.Sanitize();
            return sessions;
        }
        catch
        {
            try { File.Move(path, path + ".corrupt-" + Guid.NewGuid().ToString("N")); }
            catch { /* preserve when possible; never prevent launch */ }
            return new DualSessions();
        }
    }

    public static void Save(string path, DualSessions sessions)
    {
        sessions.Sanitize();
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(sessions, Options));
    }
}

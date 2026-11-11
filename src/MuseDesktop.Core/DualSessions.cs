using System.Text.Json;

namespace MuseDesktop.Core;

/// <summary>Local routes/preferences and explicitly written scratchpad notes; never composer drafts or sign-in data.</summary>
public sealed class DualSession
{
    public string LeftRoute { get; set; } = "/";
    public string RightRoute { get; set; } = "/";
    public double LeftFraction { get; set; } = 0.5;
    public double LeftZoom { get; set; } = 1;
    public double RightZoom { get; set; } = 1;
    public string? LeftProfileId { get; set; }
    public string? RightProfileId { get; set; }
    public bool Vertical { get; set; }
    public bool Swapped { get; set; }
    public bool RestoreOnLaunch { get; set; }
    public bool WasOpen { get; set; }
    public double WindowLeft { get; set; }
    public double WindowTop { get; set; }
    public double WindowWidth { get; set; } = 1360;
    public double WindowHeight { get; set; } = 840;
    public string Scratchpad { get; set; } = "";
    public List<DualPair> Pairs { get; set; } = new();

    public void Sanitize()
    {
        LeftRoute = CleanRoute(LeftRoute);
        RightRoute = CleanRoute(RightRoute);
        LeftFraction = CleanFraction(LeftFraction);
        LeftZoom = CleanZoom(LeftZoom);
        RightZoom = CleanZoom(RightZoom);
        LeftProfileId = Profiles.IsValidId(LeftProfileId) ? LeftProfileId : null;
        RightProfileId = Profiles.IsValidId(RightProfileId) ? RightProfileId : null;
        WindowLeft = double.IsFinite(WindowLeft) ? Math.Clamp(WindowLeft, -100000, 100000) : 0;
        WindowTop = double.IsFinite(WindowTop) ? Math.Clamp(WindowTop, -100000, 100000) : 0;
        WindowWidth = double.IsFinite(WindowWidth) ? Math.Clamp(WindowWidth, 840, 10000) : 1360;
        WindowHeight = double.IsFinite(WindowHeight) ? Math.Clamp(WindowHeight, 480, 10000) : 840;
        Scratchpad ??= "";
        if (Scratchpad.Length > DualWorkbench.MaxNotes) Scratchpad = Scratchpad[..DualWorkbench.MaxNotes];
        Pairs = (Pairs ?? new()).Where(p => p is not null).Take(20).ToList();
        foreach (var pair in Pairs) pair.Sanitize();
        Pairs = Pairs.Where(p => p.Name.Length > 0).DistinctBy(p => p.Name.ToUpperInvariant()).ToList();
    }

    public static string CleanRoute(string? route) =>
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

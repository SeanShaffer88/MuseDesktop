using System.Text.Json;

namespace MuseDesktop.Core;

// ───────────────────── launch intents (S11 #99/#100) ─────────────────────
//
// One intent model for every way something outside the process can ask
// MuseDesktop to do something: a second launch's command line, a
// Jump List task (which is a command line), a desktop shortcut (also a
// command line), and a musedesktop:// link. The launching process
// reduces its arguments to ONE intent; a running instance applies it
// through the single-instance wake channel. Process-level concerns
// (--background, --safe-mode) never become intents: they describe how
// a process starts, and a running instance is already started.

/// <summary>What a launch asks the app to do (S11 #99/#100).</summary>
public enum LaunchIntentKind
{
    /// <summary>Just bring the window forward (the long-standing plain second launch).</summary>
    ShowWindow,

    /// <summary>Show the window and navigate to the Muse home page.</summary>
    GoHome,

    /// <summary>Show the window and open Settings.</summary>
    OpenSettings,

    /// <summary>Show the window and open the Downloads window.</summary>
    OpenDownloads,

    /// <summary>Open Help &amp; troubleshooting.</summary>
    ShowHelp,

    /// <summary>Show the window focused for voice input (S11 #97).</summary>
    QuickDictate,

    /// <summary>Run the named profile. On a running instance this is the confirm-then-restart switch, never a silent swap.</summary>
    SwitchProfile,

    /// <summary>Close cleanly because an update is installing (R7D): the installer detected this instance, asked it to exit so the program files can be replaced, and reopens the app when the install finishes. The one intent that applies IMMEDIATELY — never queued behind the app lock (exiting reveals nothing; the lock re-engages at the next launch) and never a show/navigation: the window is not brought forward for it, including when the app is hidden in the tray.</summary>
    ExitForUpdate,
}

/// <summary>
/// One launch intent (S11 #99/#100). Serialized to the pending-intent
/// file for the single-instance hand-off, so the shape is a contract:
/// unknown kinds read back as null (a newer build's intent can never
/// confuse an older running instance — it just wakes the window).
/// </summary>
public sealed record LaunchIntent(LaunchIntentKind Kind, string? ProfileId = null)
{
    public static readonly LaunchIntent ShowWindow = new(LaunchIntentKind.ShowWindow);

    /// <summary>
    /// Reduce a command line to its intent. Precedence when several
    /// action flags combine: exit-for-update outranks everything (the
    /// installer is waiting to replace this build — any other action
    /// would be work done by a process that is about to exit), then a
    /// profile switch outranks opening a surface (the switch restarts
    /// the app — opening Settings first would be wasted work), then
    /// Settings, Downloads, Home, dictate.
    /// --help/--version never reach here (answered in-process).
    /// </summary>
    public static LaunchIntent FromCommandLine(CommandLineOptions options)
    {
        if (options.ExitForUpdate)
            return new LaunchIntent(LaunchIntentKind.ExitForUpdate);
        if (options.ProfileId is not null)
            return new LaunchIntent(LaunchIntentKind.SwitchProfile, options.ProfileId);
        if (options.OpenSettings)
            return new LaunchIntent(LaunchIntentKind.OpenSettings);
        if (options.OpenDownloads)
            return new LaunchIntent(LaunchIntentKind.OpenDownloads);
        if (options.OpenHome)
            return new LaunchIntent(LaunchIntentKind.GoHome);
        if (options.QuickDictate)
            return new LaunchIntent(LaunchIntentKind.QuickDictate);
        return ShowWindow;
    }

    // ─────────────────── musedesktop:// links (#99) ───────────────────

    /// <summary>
    /// Parse a <c>musedesktop://</c> link against the allowlist of
    /// routes. The result is either an intent, an intent plus a note
    /// (a well-formed profile link naming a profile that doesn't
    /// exist: show the window and say so — never a crash), or a
    /// refusal. A link can NEVER carry a web address: any query
    /// string or fragment is a refusal, as is any route outside the
    /// list. Profile ids are matched after lower-casing, because the
    /// ids themselves are lowercase slugs (see Profiles) and a link
    /// typed by hand shouldn't fail on capitalization.
    /// </summary>
    public static ProtocolParseResult ParseProtocolUri(string? raw, IReadOnlyCollection<string> validProfileIds)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return ProtocolParseResult.Refused("the link was empty");
        var text = raw.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(ProtocolLinks.Scheme, StringComparison.OrdinalIgnoreCase))
            return ProtocolParseResult.Refused("not a musedesktop:// link");
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return ProtocolParseResult.Refused("links can't carry extra data (no ? or # parts)");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return ProtocolParseResult.Refused("links can't carry a user name");
        // A port on a custom scheme is meaningless. Uri.Port is
        // unreliable for unregistered schemes, so the authority part
        // of the raw text is checked directly: a colon there is a
        // port (or something pretending to be one) — refused.
        var schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0)
        {
            var authority = text[(schemeEnd + 3)..].Split('/')[0];
            if (authority.Contains(':'))
                return ProtocolParseResult.Refused("links can't carry a port");
        }

        var route = uri.Host.ToLowerInvariant();
        var path = uri.AbsolutePath; // "" or "/segment..."
        switch (route)
        {
            case "open":
                return path is "" or "/"
                    ? ProtocolParseResult.Ok(ShowWindow)
                    : ProtocolParseResult.Refused($"unknown route '{DescribeForLog(text)}'");
            case "home":
                return path is "" or "/"
                    ? ProtocolParseResult.Ok(new LaunchIntent(LaunchIntentKind.GoHome))
                    : ProtocolParseResult.Refused($"unknown route '{DescribeForLog(text)}'");
            case "settings":
                return path is "" or "/"
                    ? ProtocolParseResult.Ok(new LaunchIntent(LaunchIntentKind.OpenSettings))
                    : ProtocolParseResult.Refused($"unknown route '{DescribeForLog(text)}'");
            case "downloads":
                return path is "" or "/"
                    ? ProtocolParseResult.Ok(new LaunchIntent(LaunchIntentKind.OpenDownloads))
                    : ProtocolParseResult.Refused($"unknown route '{DescribeForLog(text)}'");
            case "help":
                return path is "" or "/"
                    ? ProtocolParseResult.Ok(new LaunchIntent(LaunchIntentKind.ShowHelp))
                    : ProtocolParseResult.Refused($"unknown route '{DescribeForLog(text)}'");
            case "profile":
            {
                var segment = path.StartsWith('/') ? path[1..] : path;
                if (segment.Length == 0 || segment.Contains('/'))
                    return ProtocolParseResult.Refused("a profile link needs exactly one profile id");
                var id = Uri.UnescapeDataString(segment).ToLowerInvariant();
                if (Profiles.IsValidId(id) && validProfileIds.Contains(id))
                    return ProtocolParseResult.Ok(new LaunchIntent(LaunchIntentKind.SwitchProfile, id));
                // Well-formed shape, unknown (or impossible) id: the
                // friendliest honest outcome — show the window, log it.
                return ProtocolParseResult.Ok(ShowWindow,
                    $"the link named profile '{id}', which doesn't exist on this PC");
            }
            default:
                return ProtocolParseResult.Refused($"unknown route '{DescribeForLog(text)}'");
        }
    }

    /// <summary>
    /// The log-safe form of a link or URL-ish string: scheme + host +
    /// path only — never a query, fragment, or user info, where a
    /// crafted link could smuggle an address or a token. Bounded
    /// length like all log text.
    /// </summary>
    public static string DescribeForLog(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "(empty)";
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri))
            return "(unparseable)";
        var text = $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}";
        return text.Length <= 120 ? text : text[..120];
    }

    // ─────────────────── the pending-intent hand-off ───────────────────

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed class IntentDto
    {
        public string? Kind { get; set; }
        public string? ProfileId { get; set; }
    }

    public string ToJson() => JsonSerializer.Serialize(
        new IntentDto { Kind = Kind.ToString(), ProfileId = ProfileId }, JsonOptions);

    /// <summary>Parse a pending-intent file's text. Anything unexpected — broken JSON, an unknown kind, a profile intent without an id — reads as null (the caller just wakes the window).</summary>
    public static LaunchIntent? TryFromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            var dto = JsonSerializer.Deserialize<IntentDto>(json, JsonOptions);
            if (dto?.Kind is null || !Enum.TryParse<LaunchIntentKind>(dto.Kind, ignoreCase: true, out var kind))
                return null;
            if (kind == LaunchIntentKind.SwitchProfile && string.IsNullOrWhiteSpace(dto.ProfileId))
                return null;
            return new LaunchIntent(kind, dto.ProfileId);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>The outcome of parsing a <c>musedesktop://</c> link (S11 #99): an intent (possibly just "show the window", with a note worth logging), or a refusal with its plain reason.</summary>
public sealed record ProtocolParseResult(LaunchIntent? Intent, string? Note, string? Refusal)
{
    public static ProtocolParseResult Ok(LaunchIntent intent, string? note = null) => new(intent, note, null);
    public static ProtocolParseResult Refused(string reason) => new(null, null, reason);
    public bool IsRefusal => Intent is null;
}

/// <summary>
/// The pending-intent file (S11 #99/#100): a second launch writes its
/// intent beside the settings (atomically — the SettingsStore
/// pattern), then fires the long-standing wake event; the running
/// instance takes (reads + deletes) the intent when it wakes. Writing
/// BEFORE signalling is the ordering that makes the hand-off reliable;
/// a file with no reader (written just as the app exits) is stale by
/// the next launch and simply overwritten by the next intent.
/// </summary>
public static class PendingIntentStore
{
    public static void Save(string path, LaunchIntent intent)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, intent.ToJson());
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Read and remove the pending intent. Missing, corrupt, or unreadable all read as null — the caller just wakes the window, which is the pre-S11 behavior and always safe.</summary>
    public static LaunchIntent? Take(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var json = File.ReadAllText(path);
            try { File.Delete(path); }
            catch { /* a stuck file is overwritten by the next intent */ }
            return LaunchIntent.TryFromJson(json);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>Whether an arriving intent may run now or must wait behind the app lock (S11 #99/#100).</summary>
public enum IntentDisposition
{
    /// <summary>Apply it right away.</summary>
    ApplyNow,

    /// <summary>The app is locked: nothing applies — the intent is queued and the lock screen is what the user gets.</summary>
    QueueBehindLock,
}

public static class LaunchIntentRouting
{
    /// <summary>
    /// Whether an intent applies the moment it arrives, whatever the
    /// lock state (R7D). Only the exit-for-update intent qualifies:
    /// applying it shows nothing, navigates nowhere, and names no
    /// profile — the process simply closes, and the app lock
    /// re-engages on the next launch exactly as it does after any
    /// exit. Every other intent can put content on screen or reveal
    /// that a profile exists, so every other intent waits behind the
    /// lock.
    /// </summary>
    public static bool AppliesImmediately(LaunchIntent intent) =>
        intent.Kind == LaunchIntentKind.ExitForUpdate;

    /// <summary>
    /// The lock rule (S7 #64 extended by S11): while the app lock is
    /// engaged, NO intent applies — not even a profile switch or the
    /// dictate shortcut, which could leak which profile exists or put
    /// content on screen. The intent queues (last one wins, see
    /// <see cref="PendingIntentQueue"/>) and applies after unlock.
    /// The single exception is exit-for-update (R7D, see
    /// <see cref="AppliesImmediately"/>): an update the user already
    /// accepted must not stall behind a lock screen nobody may be
    /// present to answer.
    /// </summary>
    public static IntentDisposition Decide(LaunchIntent intent, bool appLocked) =>
        AppliesImmediately(intent) || !appLocked
            ? IntentDisposition.ApplyNow
            : IntentDisposition.QueueBehindLock;
}

/// <summary>
/// The behind-the-lock intent queue (S11 #99): a single slot, last
/// intent wins. One slot is deliberate — intents are "take me
/// somewhere" requests, and replaying a whole backlog after unlock
/// (Settings, then Downloads, then a profile switch) would be chaos;
/// the newest request is what the user most recently meant.
/// </summary>
public sealed class PendingIntentQueue
{
    private LaunchIntent? _queued;

    public bool HasQueued => _queued is not null;

    public LaunchIntent? Peek => _queued;

    /// <summary>Offer an intent; replaces any earlier queued one (last wins).</summary>
    public void Offer(LaunchIntent intent) => _queued = intent;

    /// <summary>Remove and return the queued intent, or null when the queue is empty.</summary>
    public LaunchIntent? Take()
    {
        var intent = _queued;
        _queued = null;
        return intent;
    }
}

// ───────────────────── protocol registration (S11 #99) ─────────────────────

/// <summary>
/// The <c>musedesktop://</c> registration policy (S11 #99). The
/// registration itself lives under HKCU <c>Software\Classes</c>
/// (per-user, no admin) and is written by the installer and kept in
/// sync by the app per the Settings toggle. Everything decidable is
/// here: what the registered command must look like, how to classify
/// what's actually registered, and what sync should do. The one rule
/// that outranks the toggle: a registration pointing at a DIFFERENT
/// program is never stomped and never removed by us.
/// </summary>
public static class ProtocolLinks
{
    public const string Scheme = "musedesktop";

    /// <summary>The HKCU classes key for the scheme (relative to HKEY_CURRENT_USER).</summary>
    public const string ClassesKeyPath = @"Software\Classes\musedesktop";

    /// <summary>The shell\open\command value this app registers: the exe path, quoted, with the link as %1.</summary>
    public static string CommandValue(string exePath) => $"\"{exePath}\" \"%1\"";

    public static ProtocolLinkState Classify(string? registeredCommand, string exePath)
    {
        if (string.IsNullOrWhiteSpace(registeredCommand))
            return ProtocolLinkState.NotRegistered;
        return string.Equals(registeredCommand.Trim(), CommandValue(exePath), StringComparison.OrdinalIgnoreCase)
            ? ProtocolLinkState.RegisteredToThisApp
            : ProtocolLinkState.RegisteredToAnotherApp;
    }

    /// <summary>What a sync pass should do for (the user's toggle, the actual state).</summary>
    public static ProtocolSyncAction DecideSync(bool wantRegistered, ProtocolLinkState state) =>
        (wantRegistered, state) switch
        {
            (true, ProtocolLinkState.NotRegistered) => ProtocolSyncAction.Register,
            (false, ProtocolLinkState.RegisteredToThisApp) => ProtocolSyncAction.Unregister,
            // Already as wanted — or registered to ANOTHER app, which
            // we never touch in either direction.
            _ => ProtocolSyncAction.None,
        };

    /// <summary>The Settings status line for the links toggle — plain words for each combination.</summary>
    public static string StatusText(bool wantRegistered, ProtocolLinkState state) =>
        (wantRegistered, state) switch
        {
            (true, ProtocolLinkState.RegisteredToThisApp) =>
                "Links like musedesktop://settings open MuseDesktop on this PC.",
            (true, ProtocolLinkState.NotRegistered) =>
                "Links are on, but not registered yet — MuseDesktop registers them when it starts.",
            (true, ProtocolLinkState.RegisteredToAnotherApp) =>
                "musedesktop:// links currently open a different program. MuseDesktop left that registration alone — the other program owns these links until it (or Windows) changes it.",
            (false, ProtocolLinkState.RegisteredToThisApp) =>
                "Links are off; the registration pointing here is removed when MuseDesktop starts.",
            (false, ProtocolLinkState.RegisteredToAnotherApp) =>
                "Links are off here, and musedesktop:// links currently open a different program (MuseDesktop didn't change that).",
            _ =>
                "Links are off: musedesktop:// links don't open MuseDesktop.",
        };
}

public enum ProtocolLinkState
{
    NotRegistered,
    RegisteredToThisApp,
    RegisteredToAnotherApp,
}

public enum ProtocolSyncAction
{
    None,
    Register,
    Unregister,
}

// ───────────────────── quick dictate wording (S11 #97) ─────────────────────

/// <summary>
/// The Quick dictate status lines (S11 #97). Quick dictate shows the
/// window and focuses the Muse page — it cannot press Muse's voice
/// button or start a dictation (no script ever runs in the page), so
/// the wording says exactly where the hand-off to Muse happens.
/// </summary>
public static class QuickDictateText
{
    /// <summary>The microphone is Muse's to use in this profile (granted this session, or Muse's saved choice is Allow): focus is all that's needed.</summary>
    public static string ReadyNote =>
        "Muse has the focus — Muse's own voice button starts dictation (MuseDesktop never starts it for you).";

    /// <summary>The microphone hasn't been granted to Muse yet in this profile: the one-time grant has to happen through Muse's own button.</summary>
    public static string MicGrantNeededNote =>
        "Voice needs the microphone — use Muse's voice button once to grant it, then dictation is one click away.";

    public static string Note(bool microphoneReady) =>
        microphoneReady ? ReadyNote : MicGrantNeededNote;
}

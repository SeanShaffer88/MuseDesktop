namespace MuseDesktop.Core;

// ───────────────────── shortcut gestures (S6 #52/#53) ─────────────────────

/// <summary>Modifier keys in a shortcut gesture. Canonical text order is Ctrl, Alt, Shift.</summary>
[Flags]
public enum GestureModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
}

/// <summary>
/// One keyboard gesture (v0.10.0, S6 #53): modifiers plus a key NAME.
/// Key names are System.Windows.Input.Key enum names ("D", "Home",
/// "OemComma", "F5", …) so the shell can map them 1:1, while Core only
/// allows a known-safe subset — letters, digits, F1–F24, navigation keys
/// and the OEM punctuation the shell's bindings use. Canonical text
/// form: "Ctrl+Shift+D", "Alt+Home", "F8" (used in settings storage).
/// </summary>
public readonly record struct ShortcutGesture(GestureModifiers Modifiers, string Key)
{
    /// <summary>Key names a gesture may use (canonical casing). Anything else never parses.</summary>
    private static readonly HashSet<string> AllowedKeys = BuildAllowedKeys();

    private static HashSet<string> BuildAllowedKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var c = 'A'; c <= 'Z'; c++) keys.Add(c.ToString());
        for (var i = 0; i <= 9; i++)
        {
            keys.Add($"D{i}");
            keys.Add($"NumPad{i}");
        }
        for (var i = 1; i <= 24; i++) keys.Add($"F{i}");
        foreach (var name in new[]
                 {
                     "Home", "End", "PageUp", "PageDown", "Insert", "Delete",
                     "Left", "Right", "Up", "Down", "Space", "Tab", "Return",
                     "OemComma", "OemPeriod", "OemQuestion", "OemPlus", "OemMinus",
                     "OemSemicolon", "OemQuotes", "OemOpenBrackets", "OemCloseBrackets",
                     "OemPipe", "OemTilde", "Add", "Subtract", "Multiply", "Divide", "Decimal",
                 })
            keys.Add(name);
        return keys;
    }

    public static bool IsAllowedKey(string? key) =>
        key is not null && AllowedKeys.Contains(key);

    /// <summary>Parse canonical text ("Ctrl+Shift+D"). Modifier names: Ctrl/Control, Alt, Shift — case-insensitive.</summary>
    public static bool TryParse(string? text, out ShortcutGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return false;
        var modifiers = GestureModifiers.None;
        string? key = null;
        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= GestureModifiers.Control;
                    break;
                case "alt":
                    modifiers |= GestureModifiers.Alt;
                    break;
                case "shift":
                    modifiers |= GestureModifiers.Shift;
                    break;
                default:
                    if (key is not null)
                        return false; // two key names — not a gesture
                    key = CanonicalKey(part);
                    if (key is null)
                        return false;
                    break;
            }
        }
        if (key is null)
            return false; // modifiers alone are not a gesture
        gesture = new ShortcutGesture(modifiers, key);
        return true;
    }

    /// <summary>The canonical stored form: "Ctrl+Shift+D", "Alt+Home", "F8".</summary>
    public string Format()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(GestureModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(GestureModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(GestureModifiers.Shift)) parts.Add("Shift");
        parts.Add(Key);
        return string.Join("+", parts);
    }

    /// <summary>The friendly form for people: "Ctrl+," , "Ctrl+Shift+D", "Alt+Home".</summary>
    public string DisplayText()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(GestureModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(GestureModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(GestureModifiers.Shift)) parts.Add("Shift");
        parts.Add(KeyDisplayName(Key));
        return string.Join("+", parts);
    }

    /// <summary>Friendly names for the keys people don't call by their enum name.</summary>
    public static string KeyDisplayName(string key) => key switch
    {
        "OemComma" => ",",
        "OemPeriod" => ".",
        "OemQuestion" => "/",
        "OemPlus" => "+",
        "OemMinus" => "-",
        "OemSemicolon" => ";",
        "OemQuotes" => "'",
        "OemOpenBrackets" => "[",
        "OemCloseBrackets" => "]",
        "OemPipe" => "\\",
        "OemTilde" => "`",
        "Return" => "Enter",
        "PageUp" => "Page Up",
        "PageDown" => "Page Down",
        _ when key.Length == 2 && key[0] == 'D' && char.IsDigit(key[1]) => key[1].ToString(),
        _ when key.StartsWith("NumPad", StringComparison.Ordinal) => $"NumPad {key[6..]}",
        _ => key,
    };

    /// <summary>Canonical casing for a user-typed key name, or null when the key isn't gesture-eligible.</summary>
    private static string? CanonicalKey(string part)
    {
        foreach (var allowed in AllowedKeys)
            if (string.Equals(allowed, part, StringComparison.OrdinalIgnoreCase))
                return allowed;
        // Single glyphs people type into a capture box: ",", "/", "+", "-".
        return part switch
        {
            "," => "OemComma",
            "." => "OemPeriod",
            "/" => "OemQuestion",
            "+" => "OemPlus",
            "-" => "OemMinus",
            _ => null,
        };
    }
}

// ───────────────────── the shortcut catalog (S6 #52) ─────────────────────

public enum ShortcutScope
{
    /// <summary>Works while the MuseDesktop window is in use.</summary>
    InApp,

    /// <summary>
    /// Works anywhere in Windows. MuseDesktop registers NO global
    /// hotkey today (show/hide is the tray icon and the single-instance
    /// wake signal — see DECISIONS.md), so the catalog has no entries
    /// with this scope; it exists so a future global binding has an
    /// honest place to live.
    /// </summary>
    Global,
}

/// <summary>
/// One row of the shortcut catalog (S6 #52): the single source of truth
/// the Shortcuts window renders and the remapping model (#53) works
/// against. Every entry corresponds to a real binding in the shell —
/// the S6 tests pin that the remappable defaults are what the main
/// window's key handler dispatches.
/// </summary>
public sealed record ShortcutAction(
    string Id,
    string Name,
    ShortcutScope Scope,
    bool Remappable,
    IReadOnlyList<string> Gestures)
{
    /// <summary>The gesture shown to people (first listed gesture, friendly form; multi-gesture fixed actions join with " / ").</summary>
    public string DisplayGestures => string.Join(" / ",
        Gestures.Select(g => ShortcutGesture.TryParse(g, out var parsed) ? parsed.DisplayText() : g));
}

public static class ShortcutCatalog
{
    public const string SettingsId = "settings";
    public const string DownloadsId = "downloads";
    public const string HomeId = "home";

    public static readonly IReadOnlyList<ShortcutAction> All = new[]
    {
        // Remappable (S6 #53). Settings has had Ctrl+, since v0.2.0;
        // Downloads and Home had NO keyboard gesture before v0.10.0 —
        // they gained defaults in this slice so there is something to
        // remap (see DECISIONS.md).
        new ShortcutAction(SettingsId, "Open Settings", ShortcutScope.InApp, Remappable: true,
            new[] { "Ctrl+OemComma" }),
        new ShortcutAction(DownloadsId, "Open Downloads", ShortcutScope.InApp, Remappable: true,
            new[] { "Ctrl+Shift+D" }),
        new ShortcutAction(HomeId, "Go to Muse home", ShortcutScope.InApp, Remappable: true,
            new[] { "Alt+Home" }),

        // Fixed — browser conventions and mode keys the shell has
        // always bound; listed so the Shortcuts window is complete.
        new ShortcutAction("find", "Find in page", ShortcutScope.InApp, Remappable: false,
            new[] { "Ctrl+F" }),
        new ShortcutAction("shortcuts", "Keyboard shortcuts list", ShortcutScope.InApp, Remappable: false,
            new[] { "Ctrl+OemQuestion" }),
        new ShortcutAction("print", "Print", ShortcutScope.InApp, Remappable: false,
            new[] { "Ctrl+P" }),
        new ShortcutAction("reload", "Reload", ShortcutScope.InApp, Remappable: false,
            new[] { "Ctrl+R", "F5" }),
        new ShortcutAction("restore-closed", "Restore the last closed page", ShortcutScope.InApp, Remappable: false,
            new[] { "Ctrl+Shift+T" }),
        new ShortcutAction("back", "Back", ShortcutScope.InApp, Remappable: false,
            new[] { "Alt+Left" }),
        new ShortcutAction("forward", "Forward", ShortcutScope.InApp, Remappable: false,
            new[] { "Alt+Right" }),
        new ShortcutAction("zoom-in", "Zoom in", ShortcutScope.InApp, Remappable: false,
            new[] { "Ctrl+OemPlus" }),
        new ShortcutAction("zoom-out", "Zoom out", ShortcutScope.InApp, Remappable: false,
            new[] { "Ctrl+OemMinus" }),
        new ShortcutAction("zoom-reset", "Reset zoom to 100%", ShortcutScope.InApp, Remappable: false,
            new[] { "Ctrl+D0" }),
        new ShortcutAction("focus-mode", "Focus mode (hide the toolbar and status bar)", ShortcutScope.InApp, Remappable: false,
            new[] { "F8" }),
        new ShortcutAction("fullscreen", "Full screen", ShortcutScope.InApp, Remappable: false,
            new[] { "F11" }),
        new ShortcutAction("escape", "Close find / leave focus mode or full screen", ShortcutScope.InApp, Remappable: false,
            new[] { "Escape" }),
    };

    public static ShortcutAction? Find(string? id) =>
        id is not null ? All.FirstOrDefault(a => a.Id == id) : null;

    public static IReadOnlyList<ShortcutAction> Remappable =>
        All.Where(a => a.Remappable).ToList();

    /// <summary>Every gesture a fixed action owns, parsed — remapping onto one of these is a conflict.</summary>
    public static IReadOnlyDictionary<ShortcutGesture, string> FixedGestures()
    {
        var map = new Dictionary<ShortcutGesture, string>();
        foreach (var action in All.Where(a => !a.Remappable))
        {
            foreach (var text in action.Gestures)
            {
                // "Escape" is a Key name outside the gesture-eligible
                // set on purpose: Esc can never be captured as a
                // remappable gesture, so it needs no conflict entry.
                if (ShortcutGesture.TryParse(text, out var gesture))
                    map.TryAdd(gesture, action.Name);
            }
        }
        return map;
    }
}

/// <summary>
/// The remapping model (S6 #53). Overrides live in settings as
/// action-id → canonical gesture text; everything here is pure so the
/// rules are testable: unknown ids and unparseable gestures are
/// dropped, a gesture must carry Ctrl or Alt (a bare letter would fire
/// while typing), and no two actions in the same scope may share a
/// gesture — conflicts against fixed actions count too.
/// </summary>
public static class ShortcutBindings
{
    /// <summary>The gesture an action effectively uses: its override when one survives sanitizing, else its catalog default.</summary>
    public static ShortcutGesture EffectiveGesture(
        string actionId, IReadOnlyDictionary<string, string>? overrides)
    {
        var action = ShortcutCatalog.Find(actionId)
                     ?? throw new ArgumentException($"Unknown shortcut action '{actionId}'.", nameof(actionId));
        var clean = SanitizeOverrides(overrides);
        if (clean.TryGetValue(actionId, out var text)
            && ShortcutGesture.TryParse(text, out var gesture))
            return gesture;
        return ShortcutGesture.TryParse(action.Gestures[0], out var fallback)
            ? fallback
            : throw new InvalidOperationException($"Catalog default for '{actionId}' does not parse.");
    }

    /// <summary>Effective gestures for every remappable action, in catalog order.</summary>
    public static IReadOnlyDictionary<string, ShortcutGesture> Effective(
        IReadOnlyDictionary<string, string>? overrides)
    {
        var clean = SanitizeOverrides(overrides);
        var result = new Dictionary<string, ShortcutGesture>();
        foreach (var action in ShortcutCatalog.Remappable)
            result[action.Id] = EffectiveGesture(action.Id, clean);
        return result;
    }

    /// <summary>
    /// Repair stored overrides: drop unknown/non-remappable ids,
    /// unparseable or modifier-less gestures, and gestures that collide
    /// with a fixed action or with any other action's effective gesture
    /// in the final state. Actions are considered in catalog order, each
    /// accepted override updating that action's effective gesture before
    /// the next is judged — so one action may take another's default
    /// only when the other has itself moved elsewhere, and the first
    /// claim in catalog order wins any true tie. Deterministic.
    /// </summary>
    public static Dictionary<string, string> SanitizeOverrides(
        IReadOnlyDictionary<string, string>? overrides)
    {
        var clean = new Dictionary<string, string>();
        if (overrides is null)
            return clean;
        var fixedGestures = ShortcutCatalog.FixedGestures();
        // Effective gestures, starting from catalog defaults.
        var effective = new Dictionary<string, ShortcutGesture>();
        foreach (var action in ShortcutCatalog.Remappable)
        {
            if (ShortcutGesture.TryParse(action.Gestures[0], out var fallback))
                effective[action.Id] = fallback;
        }
        foreach (var action in ShortcutCatalog.Remappable)
        {
            if (!overrides.TryGetValue(action.Id, out var text))
                continue;
            if (!ShortcutGesture.TryParse(text, out var gesture))
                continue;
            if (!HasModifier(gesture))
                continue;
            if (fixedGestures.ContainsKey(gesture))
                continue;
            var collides = effective.Any(pair => pair.Key != action.Id && pair.Value == gesture);
            if (collides)
                continue;
            clean[action.Id] = gesture.Format();
            effective[action.Id] = gesture;
        }
        return clean;
    }

    /// <summary>
    /// Try to set one action's gesture. On success <paramref name="updated"/>
    /// holds the new (sanitized) override map; on failure
    /// <paramref name="error"/> says why in plain words and nothing changes.
    /// </summary>
    public static bool TrySet(
        IReadOnlyDictionary<string, string>? overrides,
        string actionId,
        ShortcutGesture gesture,
        out Dictionary<string, string> updated,
        out string? error)
    {
        updated = SanitizeOverrides(overrides);
        error = null;
        var action = ShortcutCatalog.Find(actionId);
        if (action is null || !action.Remappable)
        {
            error = "That shortcut can't be changed.";
            return false;
        }
        if (!ShortcutGesture.IsAllowedKey(gesture.Key))
        {
            error = "That key can't be used as a shortcut.";
            return false;
        }
        if (!HasModifier(gesture))
        {
            error = $"A shortcut needs Ctrl or Alt as well — plain {ShortcutGesture.KeyDisplayName(gesture.Key)} would get in the way of typing.";
            return false;
        }
        if (ShortcutCatalog.FixedGestures().TryGetValue(gesture, out var fixedName))
        {
            error = $"{gesture.DisplayText()} is already used by {fixedName}, which can't be changed.";
            return false;
        }
        foreach (var other in ShortcutCatalog.Remappable)
        {
            if (other.Id == actionId)
                continue;
            if (EffectiveGesture(other.Id, updated) == gesture)
            {
                error = $"{gesture.DisplayText()} is already used by {other.Name}. Give one of them a different key.";
                return false;
            }
        }
        updated[actionId] = gesture.Format();
        return true;
    }

    /// <summary>Remove one action's override (back to its catalog default), sanitized.</summary>
    public static Dictionary<string, string> Without(
        IReadOnlyDictionary<string, string>? overrides, string actionId)
    {
        var clean = SanitizeOverrides(overrides);
        clean.Remove(actionId);
        return clean;
    }

    private static bool HasModifier(ShortcutGesture gesture) =>
        gesture.Modifiers.HasFlag(GestureModifiers.Control)
        || gesture.Modifiers.HasFlag(GestureModifiers.Alt);
}

// ───────────────────── reduce motion (S6 #55) ─────────────────────

/// <summary>The Reduce motion preference (S6 #55).</summary>
public enum ReduceMotionSetting
{
    /// <summary>Match the Windows "animation effects" preference (the default).</summary>
    FollowSystem,

    /// <summary>Reduce motion regardless of the Windows preference.</summary>
    On,

    /// <summary>Keep motion regardless of the Windows preference.</summary>
    Off,
}

public static class MotionPolicy
{
    /// <summary>
    /// Whether motion is reduced. The shell's actual motion surface is
    /// tiny — it has no storyboard/transition animations at all (see
    /// DECISIONS.md); what this gates today is the taskbar attention
    /// flash, the one attention effect the shell produces. Muse's page
    /// animations follow the Windows setting themselves via Chromium's
    /// prefers-reduced-motion.
    /// </summary>
    public static bool IsReduced(ReduceMotionSetting setting, bool systemAnimationsEnabled) =>
        setting switch
        {
            ReduceMotionSetting.On => true,
            ReduceMotionSetting.Off => false,
            _ => !systemAnimationsEnabled,
        };
}

// ───────────────────── announcements (S6 #54) ─────────────────────

/// <summary>
/// The exact texts announced to assistive technology (S6 #54). They
/// reuse the status-bar wording the sighted user sees for the same
/// event, minus the parts that only make sense visually ("— click to
/// open"). Kept in Core so the wording is pinned by tests.
/// </summary>
public static class AnnouncementText
{
    public static string DownloadCompleted(string fileName) =>
        $"Download complete: {fileName}";

    public static string DownloadFailed(string fileName, string? reason) =>
        string.IsNullOrWhiteSpace(reason)
            ? $"Download interrupted: {fileName}"
            : $"Download interrupted: {fileName} ({reason})";

    public static string UpdateAvailable(string tag) =>
        $"MuseDesktop {tag} is available — see the ⋯ menu";
}

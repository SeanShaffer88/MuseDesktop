namespace MuseDesktop.Core;

// ───────────────────── the global show/hide hotkey (S11 #96) ─────────────────────
//
// MuseDesktop's first TRUE global hotkey: RegisterHotKey on the main
// window's handle, so it fires from any app. The gesture lives in its
// OWN settings fields, not in the S6 ShortcutOverrides map — that map
// is the in-app remapping system, its sanitizer knows only in-app
// actions, and its catalog is pinned as in-app-only (S6 #52/#53).
// What the two systems share is the gesture type (ShortcutGesture)
// and the conflict rule, enforced here: the global gesture may never
// equal an in-app gesture, default or remapped — RegisterHotKey
// consumes the keystroke system-wide, so an overlap would silently
// eat the in-app shortcut everywhere, including in this app.

/// <summary>What resolving the saved global-hotkey state produced (S11 #96).</summary>
public enum HotkeyResolutionKind
{
    /// <summary>The user turned the hotkey off.</summary>
    Disabled,

    /// <summary>Register this gesture.</summary>
    Ready,

    /// <summary>The gesture collides with an in-app shortcut; registration would eat that shortcut. Not registered — Settings shows the conflict.</summary>
    BlockedByConflict,
}

/// <summary>The resolution of the saved state (S11 #96): the gesture is always reported (so Settings can show what WOULD be used), plus the conflict's owner when blocked.</summary>
public sealed record HotkeyResolution(HotkeyResolutionKind Kind, ShortcutGesture Gesture, string? ConflictOwner);

/// <summary>What one press of the global hotkey should do (S11 #96).</summary>
public enum HotkeyToggleAction
{
    /// <summary>Bring the window forward (it was hidden, or visible but not in front).</summary>
    ShowWindow,

    /// <summary>The window is in front: hide it to the tray.</summary>
    HideToTray,

    /// <summary>A modal dialog is open: bring the app forward, but never hide it from under the dialog.</summary>
    BringForwardOnly,

    /// <summary>The app is locked: the lock screen is the only surface a press may produce.</summary>
    ShowLockScreen,
}

public static class GlobalHotkey
{
    /// <summary>The factory gesture (S11 #96): Ctrl+Alt+M — unused by Windows, the shell, and every in-app binding.</summary>
    public const string DefaultGestureText = "Ctrl+Alt+M";

    public static ShortcutGesture DefaultGesture =>
        ShortcutGesture.TryParse(DefaultGestureText, out var gesture)
            ? gesture
            : throw new InvalidOperationException("The default global-hotkey gesture doesn't parse.");

    /// <summary>
    /// Resolve the saved state into a registration decision. Total:
    /// a stored text that doesn't parse or carries no Ctrl/Alt (the
    /// Sanitized pass should already have nulled it) falls back to
    /// the default gesture rather than disabling the feature. A
    /// conflict blocks — falling back to a DIFFERENT key than the one
    /// that conflicts would be a silent substitution.
    /// </summary>
    public static HotkeyResolution Resolve(
        bool enabled, string? storedText, IReadOnlyDictionary<string, string>? inAppOverrides)
    {
        var gesture = DefaultGesture;
        if (ShortcutGesture.TryParse(storedText, out var parsed) && HasCtrlOrAlt(parsed))
            gesture = parsed;
        if (!enabled)
            return new HotkeyResolution(HotkeyResolutionKind.Disabled, gesture, null);
        var owner = ConflictOwner(gesture, inAppOverrides);
        return owner is null
            ? new HotkeyResolution(HotkeyResolutionKind.Ready, gesture, null)
            : new HotkeyResolution(HotkeyResolutionKind.BlockedByConflict, gesture, owner);
    }

    /// <summary>
    /// Who already owns <paramref name="gesture"/> among the in-app
    /// bindings (a fixed action's name, or a remappable action's
    /// name), or null when it's free. The comparison set is the
    /// EFFECTIVE in-app gestures: a remapped action's old default is
    /// free for the hotkey to take, its new gesture is not.
    /// </summary>
    public static string? ConflictOwner(
        ShortcutGesture gesture, IReadOnlyDictionary<string, string>? inAppOverrides)
    {
        if (ShortcutCatalog.FixedGestures().TryGetValue(gesture, out var fixedName))
            return fixedName;
        foreach (var action in ShortcutCatalog.Remappable)
        {
            if (ShortcutBindings.EffectiveGesture(action.Id, inAppOverrides) == gesture)
                return action.Name;
        }
        return null;
    }

    /// <summary>
    /// Validate a gesture the user just captured for the global
    /// hotkey (Settings). Same bar as the in-app remapping (#53): a
    /// real, gesture-eligible key, Ctrl or Alt required (a bare key
    /// registered globally would eat that key in EVERY app), and no
    /// collision with the in-app bindings.
    /// </summary>
    public static bool TrySetGesture(
        IReadOnlyDictionary<string, string>? inAppOverrides,
        ShortcutGesture gesture,
        out string? error)
    {
        error = null;
        if (!ShortcutGesture.IsAllowedKey(gesture.Key))
        {
            error = "That key can't be used as a shortcut.";
            return false;
        }
        if (!HasCtrlOrAlt(gesture))
        {
            error = $"A global shortcut needs Ctrl or Alt as well — plain {ShortcutGesture.KeyDisplayName(gesture.Key)} would be captured in every app, including while you type.";
            return false;
        }
        if (ConflictOwner(gesture, inAppOverrides) is { } owner)
        {
            error = $"{gesture.DisplayText()} is already used by {owner} inside MuseDesktop — the global key would override it everywhere. Pick a different combination.";
            return false;
        }
        return true;
    }

    /// <summary>
    /// The toggle decision (S11 #96), pinned by tests: the lock
    /// outranks everything; a modal dialog means "come forward, never
    /// hide"; the plain toggle is foreground-aware so a press while
    /// another app is in front brings MuseDesktop forward instead of
    /// hiding a window the user wasn't looking at.
    /// </summary>
    public static HotkeyToggleAction DecideToggle(
        bool appLocked, bool modalOpen, bool windowVisible, bool windowForeground)
    {
        if (appLocked)
            return HotkeyToggleAction.ShowLockScreen;
        if (modalOpen)
            return HotkeyToggleAction.BringForwardOnly;
        if (windowVisible && windowForeground)
            return HotkeyToggleAction.HideToTray;
        return HotkeyToggleAction.ShowWindow;
    }

    private static bool HasCtrlOrAlt(ShortcutGesture gesture) =>
        gesture.Modifiers.HasFlag(GestureModifiers.Control)
        || gesture.Modifiers.HasFlag(GestureModifiers.Alt);
}

/// <summary>
/// The Win32 facts of RegisterHotKey (S11 #96), kept in Core so the
/// constants and the virtual-key table are unit-tested instead of
/// living as magic numbers at the P/Invoke site. Values are the
/// documented MOD_* / WM_HOTKEY / VK_* constants.
/// </summary>
public static class HotkeyNative
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;

    /// <summary>MOD_NOREPEAT: a held key registers once, not per auto-repeat. Always set by <see cref="ModifiersFor"/>.</summary>
    public const uint ModNoRepeat = 0x4000;

    /// <summary>WM_HOTKEY.</summary>
    public const int WmHotkey = 0x0312;

    /// <summary>The show/hide hotkey's registration id (arbitrary, app-chosen).</summary>
    public const int RegistrationId = 0x4D44; // "MD"

    /// <summary>The quick-note hotkey's registration id (R5 #148): a second registration on the same window handle, so it needs its own id.</summary>
    public const int RegistrationIdQuickNote = 0x4D4E; // "MN"

    /// <summary>The native modifier mask for a gesture, with MOD_NOREPEAT always included.</summary>
    public static uint ModifiersFor(ShortcutGesture gesture)
    {
        var mods = ModNoRepeat;
        if (gesture.Modifiers.HasFlag(GestureModifiers.Control)) mods |= ModControl;
        if (gesture.Modifiers.HasFlag(GestureModifiers.Alt)) mods |= ModAlt;
        if (gesture.Modifiers.HasFlag(GestureModifiers.Shift)) mods |= ModShift;
        return mods;
    }

    /// <summary>
    /// Map a gesture key NAME (a WPF Key enum name, the catalog's
    /// canonical form) to a Win32 virtual-key code. Only the
    /// gesture-eligible keys (<see cref="ShortcutGesture"/>) map;
    /// anything else returns false rather than guessing a code.
    /// </summary>
    public static bool TryVirtualKey(string? keyName, out uint virtualKey)
    {
        virtualKey = 0;
        if (string.IsNullOrEmpty(keyName))
            return false;
        if (keyName.Length == 1 && keyName[0] is >= 'A' and <= 'Z')
        {
            virtualKey = keyName[0]; // VK_A..VK_Z equal the ASCII codes
            return true;
        }
        if (keyName.Length == 2 && keyName[0] == 'D' && char.IsDigit(keyName[1]))
        {
            virtualKey = (uint)(0x30 + (keyName[1] - '0')); // VK_0..VK_9
            return true;
        }
        if (keyName.StartsWith("NumPad", StringComparison.Ordinal)
            && keyName.Length == 7 && char.IsDigit(keyName[6]))
        {
            virtualKey = (uint)(0x60 + (keyName[6] - '0')); // VK_NUMPAD0..9
            return true;
        }
        if (keyName.Length >= 2 && keyName[0] == 'F'
            && int.TryParse(keyName[1..], out var f) && f is >= 1 and <= 24)
        {
            virtualKey = (uint)(0x70 + f - 1); // VK_F1..VK_F24
            return true;
        }
        var mapped = keyName switch
        {
            "Home" => 0x24u,
            "End" => 0x23u,
            "PageUp" => 0x21u,
            "PageDown" => 0x22u,
            "Insert" => 0x2Du,
            "Delete" => 0x2Eu,
            "Left" => 0x25u,
            "Up" => 0x26u,
            "Right" => 0x27u,
            "Down" => 0x28u,
            "Space" => 0x20u,
            "Tab" => 0x09u,
            "Return" => 0x0Du,
            "OemSemicolon" => 0xBAu,
            "OemPlus" => 0xBBu,
            "OemComma" => 0xBCu,
            "OemMinus" => 0xBDu,
            "OemPeriod" => 0xBEu,
            "OemQuestion" => 0xBFu,
            "OemOpenBrackets" => 0xDBu,
            "OemPipe" => 0xDCu,
            "OemCloseBrackets" => 0xDDu,
            "OemQuotes" => 0xDEu,
            "OemTilde" => 0xC0u,
            "Add" => 0x6Bu,
            "Subtract" => 0x6Du,
            "Multiply" => 0x6Au,
            "Divide" => 0x6Fu,
            "Decimal" => 0x6Eu,
            _ => 0u,
        };
        if (mapped == 0)
            return false;
        virtualKey = mapped;
        return true;
    }
}

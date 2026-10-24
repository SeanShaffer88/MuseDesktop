using System.Windows;
using System.Windows.Input;

namespace MuseDesktop;

/// <summary>
/// Focus return after dialogs (v0.10.0, S6 #56). A modal dialog takes
/// keyboard focus; when it closes, WPF puts focus on the owner WINDOW
/// but not on the element the user was actually using — a keyboard
/// user lands back at the top of the tab order and has to walk back.
/// This helper records the invoking element before the dialog opens
/// and puts focus back on it afterwards. The WebView2 counts as an
/// element like any other: focusing it puts focus back inside the
/// page, which is where a dialog invoked from the ⋯ menu or a shortcut
/// almost always came from. Every failure here is cosmetic — focus
/// code never throws at the caller.
/// </summary>
public static class FocusReturn
{
    /// <summary>The element holding keyboard focus right now, or null.</summary>
    public static IInputElement? Capture() => Keyboard.FocusedElement;

    /// <summary>Show a modal dialog, then return focus to the element that had it when this was called.</summary>
    public static bool? ShowDialog(Window dialog) => ShowDialog(dialog, Capture());

    /// <summary>Show a modal dialog, then return focus to <paramref name="invoker"/>.</summary>
    public static bool? ShowDialog(Window dialog, IInputElement? invoker)
    {
        var result = dialog.ShowDialog();
        Restore(invoker);
        return result;
    }

    /// <summary>
    /// Put keyboard focus back on <paramref name="target"/>. When the
    /// element is gone (a menu closed, a list rebuilt), focus falls
    /// back to the main window itself rather than nowhere.
    /// </summary>
    public static void Restore(IInputElement? target)
    {
        try
        {
            if (target is UIElement { IsVisible: true } element && element.Focus())
                return;
        }
        catch { /* fall through to the window fallback */ }
        try { System.Windows.Application.Current?.MainWindow?.Focus(); }
        catch { /* focus is cosmetic; shutdown must not trip over it */ }
    }
}

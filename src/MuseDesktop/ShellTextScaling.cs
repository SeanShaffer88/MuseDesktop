using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Documents;

namespace MuseDesktop;

/// <summary>
/// Shell UI text scaling (v0.9.0, S5 #46): multiplies every LOCALLY-SET
/// FontSize in a shell window's logical tree by the user's scale, and
/// scales the window's inherited base size along with it. Each
/// element's design (unscaled) size is remembered, so applying a
/// different scale later starts from the design size — never compounds.
///
/// What this does NOT reach, honestly (see KNOWN_LIMITATIONS.md):
/// pop-up menus (they render in a separate tree — the main window
/// applies this to its ⋯ menu explicitly on open) and the WinForms
/// tray menu, whose font is drawn by Windows itself.
/// </summary>
public static class ShellTextScaling
{
    private sealed class DesignSize
    {
        public required double Value;
    }

    private static readonly ConditionalWeakTable<DependencyObject, DesignSize> DesignSizes = new();

    public static void Apply(DependencyObject root, double scale)
    {
        foreach (var node in SelfAndDescendants(root))
        {
            if (node.ReadLocalValue(TextElement.FontSizeProperty) is double design)
            {
                var original = DesignSizes.GetValue(node, _ => new DesignSize { Value = design }).Value;
                node.SetValue(TextElement.FontSizeProperty, original * scale);
            }
        }
        // The window's own (inherited) base size: elements without a
        // local FontSize follow it.
        if (root is Window window)
        {
            var baseSize = DesignSizes.GetValue(window, _ => new DesignSize { Value = System.Windows.SystemFonts.MessageFontSize }).Value;
            window.SetValue(TextElement.FontSizeProperty, baseSize * scale);
        }
        // Line spacing (R7 #170) rides the same pass: every window
        // this scaler already reaches gains the user's spacing
        // choice in the same application, computed from the font
        // sizes just set above.
        ShellLineSpacing.Apply(root);
    }

    private static IEnumerable<DependencyObject> SelfAndDescendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is DependencyObject dep)
            {
                foreach (var node in SelfAndDescendants(dep))
                    yield return node;
            }
        }
    }
}

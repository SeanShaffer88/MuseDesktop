using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MuseDesktop.Core;

namespace MuseDesktop;

/// <summary>
/// Shell text spacing (R7 #170): applies the user's Line spacing
/// choice to the app's own windows by setting each TextBlock's
/// LineHeight to font size × <see cref="ShellTextSpacing.BaseLineFactor"/>
/// × the chosen scale. No shell XAML sets LineHeight by hand (checked
/// when this landed), so the value is a pure function of the block's
/// current font size — re-applying never compounds, and returning to
/// Standard restores the natural spacing. Invoked from
/// <see cref="ShellTextScaling.Apply"/> so every window the font
/// scaler already reaches gains spacing in the same pass.
///
/// Honest scope (see Core's ShellTextSpacing and KNOWN_LIMITATIONS):
/// WPF exposes no letter-spacing property for its text controls, so
/// the control is line spacing only.
/// </summary>
public static class ShellLineSpacing
{
    /// <summary>The active scale, set from settings by Strings.Initialize and by the Settings window when the choice changes.</summary>
    public static double ScaleFactor { get; set; } = 1.0;

    /// <summary>Apply the active scale to every TextBlock under <paramref name="root"/>.</summary>
    public static void Apply(DependencyObject root)
    {
        if (root is null)
            return;
        foreach (var block in EnumerateTextBlocks(root))
        {
            var height = ShellTextSpacing.LineHeight(block.FontSize, ScaleFactor);
            block.LineHeight = height ?? double.NaN;
        }
    }

    private static IEnumerable<TextBlock> EnumerateTextBlocks(DependencyObject root)
    {
        if (root is TextBlock block)
            yield return block;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
            foreach (var child in EnumerateTextBlocks(VisualTreeHelper.GetChild(root, i)))
                yield return child;
    }
}

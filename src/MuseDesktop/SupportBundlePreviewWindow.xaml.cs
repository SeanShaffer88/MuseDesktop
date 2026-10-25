using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using MuseDesktop.Core;

namespace MuseDesktop;

/// <summary>
/// The support-bundle preview (v0.14.0, S10 #90/#95): BEFORE anything
/// is written, this dialog lists exactly what the bundle contains —
/// the file list and the exclusions statement come from Core's
/// <see cref="SupportBundle"/>, the same source the zip's manifest is
/// composed from, so the preview can't drift from the payload.
///
/// The screenshot is opt-in and off by default: checking the box
/// captures the app's OWN main window at that moment (the owner
/// supplies the capture callback — a render of the window's own
/// visual tree, never a screen grab, so other windows and the desktop
/// are physically incapable of appearing), shows the preview WITH
/// the conversation-content warning, and only a checked + confirmed
/// screenshot enters the bundle.
/// </summary>
public partial class SupportBundlePreviewWindow : Window
{
    private readonly Func<byte[]?> _captureScreenshot;

    /// <summary>True when the user confirmed with "Choose where to save…" (DialogResult mirrors it).</summary>
    public bool Confirmed { get; private set; }

    /// <summary>The reviewed screenshot PNG bytes, when the box is checked and a capture exists.</summary>
    public byte[]? ScreenshotPng { get; private set; }

    /// <summary>Whether the screenshot box ended up checked (the bundle includes it only when ScreenshotPng is also present).</summary>
    public bool IncludeScreenshot => ScreenshotCheck.IsChecked == true && ScreenshotPng is not null;

    public SupportBundlePreviewWindow(bool screenshotAvailable, string? screenshotUnavailableReason,
        Func<byte[]?> captureScreenshot)
    {
        _captureScreenshot = captureScreenshot;
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        if (!screenshotAvailable)
        {
            ScreenshotCheck.IsEnabled = false;
            ScreenshotUnavailableText.Text = screenshotUnavailableReason
                ?? "A screenshot isn't available right now.";
            ScreenshotUnavailableText.Visibility = Visibility.Visible;
        }
        RefreshPreviewText();
    }

    private void RefreshPreviewText() =>
        PreviewText.Text = SupportBundle.PreviewText(ScreenshotCheck.IsChecked == true);

    private void OnScreenshotToggled(object sender, RoutedEventArgs e)
    {
        if (ScreenshotCheck.IsChecked == true)
        {
            ScreenshotPng = _captureScreenshot();
            if (ScreenshotPng is null)
            {
                CaptureNote.Text = "The window couldn't be captured — the bundle will be written without a screenshot.";
                ScreenshotCheck.IsChecked = false;
                return;
            }
            var image = new BitmapImage();
            using (var stream = new MemoryStream(ScreenshotPng))
            {
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
            }
            image.Freeze();
            ScreenshotPreview.Source = image;
            ScreenshotPreview.Visibility = Visibility.Visible;
            ScreenshotWarningText.Visibility = Visibility.Visible;
        }
        else
        {
            ScreenshotPng = null;
            ScreenshotPreview.Source = null;
            ScreenshotPreview.Visibility = Visibility.Collapsed;
            ScreenshotWarningText.Visibility = Visibility.Collapsed;
        }
        RefreshPreviewText();
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        DialogResult = true;
        Close();
    }
}

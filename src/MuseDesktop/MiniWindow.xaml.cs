using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MuseDesktop.Core;

namespace MuseDesktop;

/// <summary>
/// The mini strip (R6 #154): a small always-on-top companion window
/// shown while the main window hides. Everything it displays is the
/// main window's own state, pulled through delegates once a second —
/// the profile color band, the page's favicon and title, and the
/// download summary whose label and button states are Core's
/// <see cref="MiniDownloadSummary"/>. Pause all / Resume all and
/// Expand are the main window's operations; this window owns no
/// state of its own beyond what's on screen. Shell chrome only:
/// there is deliberately no second web view here.
/// </summary>
public partial class MiniWindow : Window
{
    private readonly Func<string> _pageTitle;
    private readonly Func<byte[]?> _faviconPng;
    private readonly Func<string?> _profileColorHex;
    private readonly Func<MiniDownloadSummary> _downloadSummary;
    private readonly Action _pauseAll;
    private readonly Action _resumeAll;
    private readonly Action _expand;
    private readonly DispatcherTimer _refreshTimer;
    private byte[]? _shownFavicon;

    public MiniWindow(
        Func<string> pageTitle,
        Func<byte[]?> faviconPng,
        Func<string?> profileColorHex,
        Func<MiniDownloadSummary> downloadSummary,
        Action pauseAll,
        Action resumeAll,
        Action expand)
    {
        _pageTitle = pageTitle;
        _faviconPng = faviconPng;
        _profileColorHex = profileColorHex;
        _downloadSummary = downloadSummary;
        _pauseAll = pauseAll;
        _resumeAll = resumeAll;
        _expand = expand;
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        Loaded += (_, _) =>
        {
            // Rest just above the taskbar at the screen's right edge —
            // present, out of the way, and where a strip is expected.
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 12;
            Top = area.Bottom - ActualHeight - 12;
            Refresh();
        };
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, _) => Refresh();
        _refreshTimer.Start();
        Closed += (_, _) => _refreshTimer.Stop();
    }

    private void Refresh()
    {
        PageTitleText.Text = _pageTitle();
        // Fully qualified: the WinForms global usings make the short
        // Media names ambiguous with System.Drawing in this project.
        if (_profileColorHex() is { } hex &&
            System.Windows.Media.ColorConverter.ConvertFromString(hex) is System.Windows.Media.Color color)
        {
            ProfileBand.Background = new SolidColorBrush(color);
        }
        SetFavicon(_faviconPng());
        var summary = _downloadSummary();
        DownloadLabel.Text = summary.Label;
        PauseAllButton.IsEnabled = summary.CanPause;
        ResumeAllButton.IsEnabled = summary.CanResume;
    }

    private void SetFavicon(byte[]? png)
    {
        if (ReferenceEquals(png, _shownFavicon))
            return;
        _shownFavicon = png;
        if (png is null)
        {
            FaviconImage.Source = null;
            FaviconImage.Visibility = Visibility.Collapsed;
            return;
        }
        try
        {
            var image = new BitmapImage();
            using var memory = new MemoryStream(png);
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = memory;
            image.EndInit();
            FaviconImage.Source = image;
            FaviconImage.Visibility = Visibility.Visible;
        }
        catch
        {
            FaviconImage.Source = null;
            FaviconImage.Visibility = Visibility.Collapsed;
        }
    }

    private void OnPauseAll(object sender, RoutedEventArgs e)
    {
        _pauseAll();
        Refresh();
    }

    private void OnResumeAll(object sender, RoutedEventArgs e)
    {
        _resumeAll();
        Refresh();
    }

    private void OnExpand(object sender, RoutedEventArgs e) => _expand();
}

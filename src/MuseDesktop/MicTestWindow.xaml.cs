using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using MuseDesktop.Core;

namespace MuseDesktop;

/// <summary>
/// The guided microphone test (v0.14.0, S10 #92). Hosts the app's own
/// test page in a WebView2 on the ACTIVE profile's environment, so the
/// microphone request travels the real WebView2 PermissionRequested
/// path (the same event the main window's policy handler answers for
/// muse.ai). The page is served from a temporary folder under the
/// reserved virtual host <c>musedesktop.test</c>
/// (SetVirtualHostNameToFolderMapping — present in the pinned SDK
/// 1.0.4258.31), which makes it a secure context so getUserMedia is
/// available at all; THIS window's permission handler grants the
/// microphone to that test host only and denies everything else. The
/// production policy is untouched: muse.ai is granted by the main
/// window's handler, every other origin is refused.
///
/// The page asks for the mic, listens ~3 seconds measuring peak level,
/// then posts the outcome back as JSON (Core's
/// <see cref="GuidedSelfTests.ParseMicReport"/> interprets it, so the
/// result line is the tested Core wording). The temporary folder is
/// deleted when the window closes and the page stops its tracks
/// before reporting.
/// </summary>
public partial class MicTestWindow : Window
{
    private readonly CoreWebView2Environment _environment;
    private readonly DiagnosticLog _log;
    private readonly DispatcherTimer _timeout = new() { Interval = TimeSpan.FromSeconds(15) };
    private string? _tempDir;
    private bool _finished;

    /// <summary>The result line for the caller (Settings) to show + announce; null when the test never ran.</summary>
    public string? ResultLine { get; private set; }

    /// <summary>The raw outcome, for the caller/log.</summary>
    public MicTestOutcome? Outcome { get; private set; }

    public MicTestWindow(CoreWebView2Environment environment, DiagnosticLog log)
    {
        _environment = environment;
        _log = log;
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        _timeout.Tick += (_, _) => Finish(GuidedSelfTests.ParseMicReport(null));
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "MuseDesktop-MicTest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            await File.WriteAllTextAsync(Path.Combine(_tempDir, "test.html"), TestPageHtml);

            await TestView.EnsureCoreWebView2Async(_environment);
            var core = TestView.CoreWebView2;
            core.PermissionRequested += OnPermissionRequested;
            core.WebMessageReceived += OnWebMessageReceived;
            core.SetVirtualHostNameToFolderMapping(
                GuidedSelfTests.MicTestHost, _tempDir, CoreWebView2HostResourceAccessKind.Allow);
            StatusText.Text = Strings.Get("ui.asking_for_the_microphone");
            _timeout.Start();
            core.Navigate($"https://{GuidedSelfTests.MicTestHost}/test.html");
        }
        catch (Exception ex)
        {
            _log.Warn($"Microphone test couldn't start: {ex.Message}");
            Finish(MicTestOutcome.DeviceError);
        }
    }

    private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        // Scoped to this window's own test page: microphone for the
        // test host, nothing for anything else — a narrower grant than
        // the production policy's, not a widening of it.
        var isTestPage = Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)
                         && uri.Host.Equals(GuidedSelfTests.MicTestHost, StringComparison.OrdinalIgnoreCase);
        e.State = isTestPage && e.PermissionKind == CoreWebView2PermissionKind.Microphone
            ? CoreWebView2PermissionState.Allow
            : CoreWebView2PermissionState.Deny;
        e.Handled = true;
        _log.Info(isTestPage && e.PermissionKind == CoreWebView2PermissionKind.Microphone
            ? "Microphone test: granted the microphone to the built-in test page"
            : $"Microphone test: denied {e.PermissionKind} for {UrlPolicy.RedactForLog(e.Uri)}");
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? message = null;
        try { message = e.TryGetWebMessageAsString(); }
        catch { /* not a string message — not ours */ }
        if (message is null || !message.Contains("\"mic-result\"", StringComparison.Ordinal))
            return;
        Finish(GuidedSelfTests.ParseMicReport(message));
    }

    private void Finish(MicTestOutcome outcome)
    {
        if (_finished)
            return;
        _finished = true;
        _timeout.Stop();
        Outcome = outcome;
        ResultLine = SelfTestLines.Microphone(outcome);
        StatusText.Text = outcome is MicTestOutcome.HeardSound or MicTestOutcome.GrantedButSilent
            ? Strings.Get("mictest.finished")
            : Strings.Get("mictest.finished_failed");
        ResultText.Text = ResultLine;
        _log.Info($"Microphone test outcome: {outcome}");
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnClosed(object? sender, EventArgs e)
    {
        _timeout.Stop();
        try { TestView.Dispose(); } catch { /* already gone */ }
        if (_tempDir is not null)
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch { /* a temp folder left behind is tidied by Windows eventually */ }
        }
    }

    /// <summary>
    /// The built-in test page: asks for the mic, samples the peak
    /// level for ~3 s, posts one JSON result, stops its tracks. Plain
    /// words on the page itself so the user sees the state live.
    /// </summary>
    private const string TestPageHtml = """
        <!doctype html>
        <html>
        <head><meta charset="utf-8"><title>Microphone test</title></head>
        <body style="font-family:'Segoe UI',sans-serif;background:#141417;color:#F2F2F3;margin:10px;font-size:13px">
        <div id="s">Starting the microphone test…</div>
        <script>
        const post = o => { try { window.chrome.webview.postMessage(JSON.stringify(o)); } catch (e) { } };
        (async () => {
          const el = document.getElementById('s');
          try {
            const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
            el.textContent = 'Listening — say something…';
            const ctx = new (window.AudioContext || window.webkitAudioContext)();
            const src = ctx.createMediaStreamSource(stream);
            const an = ctx.createAnalyser(); an.fftSize = 2048; src.connect(an);
            const buf = new Uint8Array(an.fftSize);
            let peak = 0; const t0 = performance.now();
            const tick = () => {
              an.getByteTimeDomainData(buf);
              for (let i = 0; i < buf.length; i++) { const v = Math.abs(buf[i] - 128) / 128; if (v > peak) peak = v; }
              if (performance.now() - t0 < 3000) requestAnimationFrame(tick);
              else {
                stream.getTracks().forEach(t => t.stop());
                ctx.close();
                el.textContent = 'Done — you can close this window.';
                post({ type: 'mic-result', ok: true, peak: peak });
              }
            };
            tick();
          } catch (err) {
            el.textContent = 'The microphone could not be started.';
            post({ type: 'mic-result', ok: false, error: (err && err.name) || 'Error' });
          }
        })();
        </script>
        </body>
        </html>
        """;
}

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using MuseDesktop.Core;
using ComboBox = System.Windows.Controls.ComboBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace MuseDesktop;

/// <summary>
/// Compare mode (v2.1.0, capability #8; design Concept B — a separate
/// window, SEAN delegated the pick 2026-11-02): the same prompt to
/// two profiles at once, the answers side by side. Each pane is a
/// WebView2 on its own profile's environment — built exactly the way
/// the main window builds its own (<see cref="CoreWebView2Environment.CreateAsync"/>
/// over <see cref="AppPaths.ProfileDir"/>; the same folder resolves
/// to the same environment in-process, so a pane on the active
/// profile shares the main window's sign-in) — navigated to a fresh
/// conversation whenever its profile is chosen.
///
/// The panes are PLAIN pages in v1: the page bridge is never
/// installed in them, so there are no drafts, follow-up chips,
/// page-health probes, or restyles here, and nothing reads the
/// answers back. The one script that runs is the send (built by
/// this window, below), fired only by the
/// "Send to both" button — that click IS the user's send, the app's
/// one deliberate exception to "inserts land unsent". The prompt is
/// never logged and never stored; the per-pane status chips (Core's
/// <see cref="CompareMode"/> mapping) are the whole report, and the
/// prompt box keeps its text unless BOTH panes report a send.
/// </summary>
public partial class CompareWindow : Window
{
    /// <summary>One profile-picker row: the id the pane runs on, and the label switchers show for it.</summary>
    private sealed class ProfileChoice
    {
        public required string Id { get; init; }
        public required string Label { get; init; }
    }

    /// <summary>One pane's moving parts. A WebView2 control is bound to a single environment for life, so a profile change retires the control and builds a fresh one; <see cref="Generation"/> keeps a slow start from landing in a pane that has already moved on.</summary>
    private sealed class Pane
    {
        public required ComboBox Picker { get; init; }
        public required TextBlock Status { get; init; }
        public required Grid Host { get; init; }
        public WebView2? Browser;
        public CoreWebView2? Core;
        public string ProfileId = "";
        public bool Loading;
        public int Generation;
    }

    private readonly DiagnosticLog _log;
    private readonly IReadOnlyList<AccountProfile> _profiles;
    private readonly Dictionary<string, Task<CoreWebView2Environment>> _environments = new();
    private readonly Pane _left;
    private readonly Pane _right;
    private bool _initializing = true;
    private bool _sending;
    private bool _closed;

    public CompareWindow(AppSettings settings, DiagnosticLog log)
    {
        _log = log;
        _profiles = Profiles.VisibleProfiles(settings);
        InitializeComponent();
        _left = new Pane { Picker = LeftProfileCombo, Status = LeftStatus, Host = LeftWebHost };
        _right = new Pane { Picker = RightProfileCombo, Status = RightStatus, Host = RightWebHost };
        foreach (var pane in new[] { _left, _right })
        {
            pane.Picker.ItemsSource = _profiles
                .Select(p => new ProfileChoice { Id = p.Id, Label = Profiles.DisplayLabel(p) })
                .ToList();
        }
        // Core's defaulting rule: two DIFFERENT profiles when the
        // user has two (the left starts on the active profile).
        var (leftId, rightId) = CompareMode.DefaultPaneProfiles(_profiles, settings.ActiveProfileId);
        SelectProfile(_left, leftId);
        SelectProfile(_right, rightId);
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void SelectProfile(Pane pane, string profileId) =>
        pane.Picker.SelectedItem = ((List<ProfileChoice>)pane.Picker.ItemsSource)
            .FirstOrDefault(c => c.Id == profileId);

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _initializing = false;
        if (_left.Picker.SelectedItem is ProfileChoice left)
            await StartPaneAsync(_left, left.Id);
        if (_right.Picker.SelectedItem is ProfileChoice right)
            await StartPaneAsync(_right, right.Id);
        PromptBox.Focus();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        foreach (var pane in new[] { _left, _right })
            RetireBrowser(pane);
        // The environments are NOT disposed: they are shared
        // process-wide (a pane on the active profile holds the main
        // window's own environment object), and the main window
        // never disposes its either.
    }

    // ───────────────────────── panes ─────────────────────────

    /// <summary>
    /// The environment for a profile, created the way the main
    /// window creates its own and cached per folder for this
    /// window's life. A faulted creation is not cached — the next
    /// try starts clean, the main window's discipline.
    /// </summary>
    private Task<CoreWebView2Environment> GetEnvironmentAsync(string profileId)
    {
        if (!_environments.TryGetValue(profileId, out var task))
        {
            Directory.CreateDirectory(AppPaths.ProfileDir(profileId));
            task = CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: AppPaths.ProfileDir(profileId));
            _environments[profileId] = task;
            _ = task.ContinueWith(t =>
            {
                if (t.IsFaulted && _environments.TryGetValue(profileId, out var current)
                    && ReferenceEquals(current, task))
                    _environments.Remove(profileId);
            }, TaskScheduler.Default);
        }
        return task;
    }

    private void RetireBrowser(Pane pane)
    {
        if (pane.Browser is null)
            return;
        pane.Host.Children.Remove(pane.Browser);
        try { pane.Browser.Dispose(); } catch { /* already dead */ }
        pane.Browser = null;
        pane.Core = null;
    }

    /// <summary>Point a pane at a profile: fresh web view on that profile's environment, fresh conversation. Any earlier start for the pane is superseded via the generation counter.</summary>
    private async Task StartPaneAsync(Pane pane, string profileId)
    {
        var generation = ++pane.Generation;
        pane.ProfileId = profileId;
        pane.Loading = true;
        pane.Status.Text = "";
        RetireBrowser(pane);

        var browser = new WebView2 { DefaultBackgroundColor = ThemeManager.BrowserBackground };
        AutomationProperties.SetName(browser, Strings.Get("muse_web_content"));
        pane.Host.Children.Add(browser);
        pane.Browser = browser;
        try
        {
            var env = await GetEnvironmentAsync(profileId);
            if (_closed || generation != pane.Generation)
            {
                RetireBrowser(pane);
                return;
            }
            await browser.EnsureCoreWebView2Async(env);
            if (_closed || generation != pane.Generation)
                return;
            var core = browser.CoreWebView2;
            pane.Core = core;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsBuiltInErrorPageEnabled = false;
            core.NewWindowRequested += (_, e) => OnPaneNewWindowRequested(pane, e);
            core.NavigationStarting += (_, _) =>
            {
                if (generation == pane.Generation)
                    pane.Loading = true;
            };
            core.NavigationCompleted += (_, _) =>
            {
                if (generation == pane.Generation)
                    pane.Loading = false;
            };
            core.Navigate(UrlPolicy.HomeUrl);
            _log.Info($"Compare pane opened on profile {profileId}: {UrlPolicy.RedactForLog(UrlPolicy.HomeUrl)}");
        }
        catch (Exception ex)
        {
            if (!_closed && generation == pane.Generation)
            {
                pane.Loading = false;
                pane.Status.Text = Strings.Get("compare.status_failed_to_start");
                _log.Error($"Compare pane failed to start: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>New-window requests inside a pane follow the same UrlPolicy routing the secondary window uses: in-app links stay in the pane, external ones go to the browser, the rest are refused.</summary>
    private void OnPaneNewWindowRequested(Pane pane, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        switch (UrlPolicy.ClassifyNewWindow(e.Uri))
        {
            case LinkDisposition.InApp:
                pane.Core?.Navigate(e.Uri);
                break;
            case LinkDisposition.ExternalBrowser:
                try { Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true }); }
                catch { /* no default browser etc. */ }
                break;
            default:
                _log.Warn($"Blocked new-window request (compare): {UrlPolicy.RedactForLog(e.Uri)}");
                break;
        }
    }

    private void OnLeftProfileChanged(object sender, SelectionChangedEventArgs e) =>
        OnPaneProfileChanged(_left);

    private void OnRightProfileChanged(object sender, SelectionChangedEventArgs e) =>
        OnPaneProfileChanged(_right);

    private void OnPaneProfileChanged(Pane pane)
    {
        if (_initializing || _closed)
            return;
        if (pane.Picker.SelectedItem is ProfileChoice choice && choice.Id != pane.ProfileId)
            _ = StartPaneAsync(pane, choice.Id);
    }

    // ───────────────────────── the shared send ─────────────────────────

    private void OnPromptChanged(object sender, TextChangedEventArgs e) =>
        SendButton.IsEnabled = !_sending && PromptBox.Text.Trim().Length > 0;

    private void OnPromptKeyDown(object sender, KeyEventArgs e)
    {
        // Enter sends (the composer's own habit); Shift+Enter is a newline.
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            _ = SendToBothAsync();
        }
    }

    private async void OnSendToBoth(object sender, RoutedEventArgs e) =>
        await SendToBothAsync();

    private async Task SendToBothAsync()
    {
        var prompt = PromptBox.Text;
        if (_sending || prompt.Trim().Length == 0)
            return;
        _sending = true;
        SendButton.IsEnabled = false;
        try
        {
            var leftOutcome = await SendToPaneAsync(_left, prompt);
            var rightOutcome = await SendToPaneAsync(_right, prompt);
            _left.Status.Text = Strings.Get(CompareMode.StatusKey(leftOutcome));
            _right.Status.Text = Strings.Get(CompareMode.StatusKey(rightOutcome));
            // The prompt is the user's only copy of the question:
            // it clears only on a clean double send (Core's rule).
            if (CompareMode.BothSent(leftOutcome, rightOutcome))
                PromptBox.Clear();
            // Outcomes only — the prompt itself is never logged.
            _log.Info($"Compare send: left={leftOutcome}, right={rightOutcome}");
        }
        finally
        {
            _sending = false;
            SendButton.IsEnabled = PromptBox.Text.Trim().Length > 0;
        }
    }

    /// <summary>Run the send one-shot in one pane and read its report through Core's mapping: "sent" is a send; anything else is "still loading" while the pane is loading and "no message box" once it has settled.</summary>
    private async Task<CompareSendOutcome> SendToPaneAsync(Pane pane, string prompt)
    {
        var core = pane.Core;
        if (core is null)
            return CompareMode.OutcomeFor(null, pageStillLoading: true);
        string? token = null;
        try
        {
            var raw = await core.ExecuteScriptAsync(BuildSendScript(prompt));
            if (raw is not null && raw != "null")
                token = JsonSerializer.Deserialize<string>(raw);
        }
        catch (Exception ex)
        {
            // A pane mid-navigation refuses the run; the outcome
            // mapping reads that through the loading flag.
            _log.Warn($"Compare send script failed on a pane: {ex.GetType().Name}: {ex.Message}");
        }
        return CompareMode.OutcomeFor(token, pane.Loading);
    }

    /// <summary>
    /// The send one-shot. It deliberately does NOT live in
    /// PageScripts: that file carries the bridge's file-level
    /// never-clicks invariant (an R7C guard pins it), and this is
    /// the one script in the app that DOES click — the page's own
    /// send control — because the "Send to both" click it runs from
    /// is itself the user's send. The compare panes never get the
    /// bridge, so the script carries its own composer finder/setter
    /// (the bridge's, copied: same structural selectors, same
    /// native-setter discipline). It sets the composer to the
    /// prompt, then clicks a visible, enabled button whose label
    /// names sending (the composer's form first, then the page);
    /// with no such control it falls back to an Enter key sequence
    /// on the composer. The result is the JSON string "sent",
    /// "no-composer", or "error" — read by Core's
    /// <see cref="CompareMode.OutcomeFor"/>; the prompt rides in as
    /// a JSON-serialized argument and is never logged.
    /// </summary>
    private static string BuildSendScript(string prompt) =>
        "(function (text) {\n" +
        "  'use strict';\n" +
        "  try {\n" +
        "    function findComposer() {\n" +
        "      var candidates = document.querySelectorAll(\n" +
        "        'form textarea, form [contenteditable=\"true\"], textarea, [contenteditable=\"true\"][role=\"textbox\"], div[contenteditable=\"true\"]');\n" +
        "      var visible = [];\n" +
        "      for (var i = 0; i < candidates.length; i++) {\n" +
        "        var c = candidates[i];\n" +
        "        if (c.offsetParent !== null || c === document.activeElement) visible.push(c);\n" +
        "      }\n" +
        "      return visible.length ? visible[visible.length - 1] : null;\n" +
        "    }\n" +
        "    function setComposerText(el, text) {\n" +
        "      if (typeof el.value === 'string') {\n" +
        "        var setter = Object.getOwnPropertyDescriptor(window.HTMLTextAreaElement.prototype, 'value').set;\n" +
        "        setter.call(el, text);\n" +
        "      } else {\n" +
        "        el.focus();\n" +
        "        var sel = window.getSelection();\n" +
        "        sel.selectAllChildren(el);\n" +
        "        document.execCommand('insertText', false, text);\n" +
        "      }\n" +
        "      el.dispatchEvent(new Event('input', { bubbles: true }));\n" +
        "    }\n" +
        "    var el = findComposer();\n" +
        "    if (!el) return 'no-composer';\n" +
        "    setComposerText(el, text);\n" +
        "    function findSendButton(scope) {\n" +
        "      if (!scope) return null;\n" +
        "      var buttons = scope.querySelectorAll('button');\n" +
        "      for (var i = 0; i < buttons.length; i++) {\n" +
        "        var b = buttons[i];\n" +
        "        if (b.disabled || b.offsetParent === null) continue;\n" +
        "        var label = ((b.getAttribute('aria-label') || '') + ' ' + (b.textContent || '')).toLowerCase();\n" +
        "        if (label.indexOf('send') >= 0) return b;\n" +
        "      }\n" +
        "      return null;\n" +
        "    }\n" +
        "    var form = el.closest ? el.closest('form') : null;\n" +
        "    var sendBtn = findSendButton(form) || findSendButton(document);\n" +
        "    if (sendBtn) { sendBtn.click(); return 'sent'; }\n" +
        "    el.focus();\n" +
        "    var opts = { key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true, cancelable: true };\n" +
        "    el.dispatchEvent(new KeyboardEvent('keydown', opts));\n" +
        "    el.dispatchEvent(new KeyboardEvent('keypress', opts));\n" +
        "    el.dispatchEvent(new KeyboardEvent('keyup', opts));\n" +
        "    return 'sent';\n" +
        "  } catch (e) { return 'error'; }\n" +
        "})(" + JsonSerializer.Serialize(prompt) + ")";
}

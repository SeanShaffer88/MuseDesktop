using System.Windows;
using System.Windows.Threading;
using MuseDesktop.Core;

namespace MuseDesktop;

/// <summary>
/// The app-lock screen (v0.11.0, S7 #64). Shown (a) modally at
/// startup, before the main window — and therefore WebView2 — exists,
/// and (b) whenever the app locks mid-session. It checks the typed
/// credential against the stored PBKDF2 record via Core's
/// <see cref="AppLock"/> and applies Core's <see cref="AppLockThrottle"/>:
/// 5 wrong tries → the Unlock button disables for a 30-second
/// countdown. No lockout-forever, no data wipe, ever.
///
/// Closing without unlocking is safe in both modes: at startup the
/// caller exits the app (no locked zombie); mid-session the app stays
/// in the tray, locked, and the tray icon reopens this window.
/// </summary>
public partial class LockWindow : Window
{
    private readonly AppLockRecord _record;
    private readonly AppLockThrottle _throttle;
    private readonly DispatcherTimer _cooldownTimer;

    /// <param name="record">The stored credential record to check against.</param>
    /// <param name="throttle">The session's throttle (shared across lock screens, so closing and reopening doesn't reset the count).</param>
    /// <param name="exitOnClose">Startup mode: the leave button reads "Exit MuseDesktop" (closing exits the app). Mid-session it reads "Stay locked".</param>
    public LockWindow(AppLockRecord record, AppLockThrottle throttle, bool exitOnClose)
    {
        _record = record;
        _throttle = throttle;
        InitializeComponent();
        LeaveButton.Content = exitOnClose ? Strings.Get("lock.exit_app") : Strings.Get("stay_locked");
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        Loaded += (_, _) =>
        {
            PasswordEntry.Focus();
            RefreshCooldownUi();
        };
        _cooldownTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _cooldownTimer.Tick += (_, _) => RefreshCooldownUi();
    }

    /// <summary>True once the right credential was typed (the caller checks this after the window closes).</summary>
    public bool Unlocked { get; private set; }

    private void OnPasswordKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            e.Handled = true;
            TryUnlock();
        }
    }

    private void OnUnlock(object sender, RoutedEventArgs e) => TryUnlock();

    private void OnLeave(object sender, RoutedEventArgs e) => Close();

    private void TryUnlock()
    {
        var now = DateTimeOffset.UtcNow;
        if (_throttle.IsCoolingDown(now))
        {
            RefreshCooldownUi();
            return;
        }
        if (AppLock.Verify(PasswordEntry.Password, _record))
        {
            _throttle.RecordSuccess();
            Unlocked = true;
            _cooldownTimer.Stop();
            // Modal at startup: DialogResult ends ShowDialog. Shown
            // modeless mid-session: DialogResult throws, so just close
            // (the Closed handler reads Unlocked).
            try { DialogResult = true; }
            catch (InvalidOperationException) { Close(); }
            return;
        }
        var startedCooldown = _throttle.RecordFailure(now);
        PasswordEntry.Clear();
        PasswordEntry.Focus();
        StatusText.Text = startedCooldown
            ? Strings.Get("lock.wrong_cooldown")
            : Strings.Format("lock.wrong_count", _throttle.ConsecutiveFailures, AppLockThrottle.FailuresBeforeCooldown);
        RefreshCooldownUi();
    }

    /// <summary>While the throttle cools down, the button is disabled and the status counts the seconds down.</summary>
    private void RefreshCooldownUi()
    {
        var now = DateTimeOffset.UtcNow;
        if (_throttle.IsCoolingDown(now))
        {
            UnlockButton.IsEnabled = false;
            var seconds = Math.Max(1, (int)Math.Ceiling(_throttle.RemainingCooldown(now).TotalSeconds));
            StatusText.Text = Strings.Format("ui.too_many_wrong_tries", seconds);
            if (!_cooldownTimer.IsEnabled)
                _cooldownTimer.Start();
        }
        else
        {
            UnlockButton.IsEnabled = true;
            _cooldownTimer.Stop();
            if (StatusText.Text.StartsWith(Strings.Get("ui.too_many_wrong_tries").Split('{')[0].TrimEnd(), StringComparison.Ordinal))
                StatusText.Text = Strings.Get("ui.you_can_try_again_now");
        }
    }
}

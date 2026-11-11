using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using MuseDesktop.Core;
using CheckBox = System.Windows.Controls.CheckBox;

namespace MuseDesktop;

public partial class SettingsWindow
{
    private CheckBox? _autoApprovalCheck;
    private bool _syncingAutoApproval;

    private void SetupAutoApprovalOption()
    {
        _autoApprovalCheck = new CheckBox
        {
            Content = Strings.Get("approval.auto_label"),
            ToolTip = Strings.Get("approval.auto_hint"),
            Margin = new Thickness(0, 8, 0, 0),
        };
        AutomationProperties.SetName(_autoApprovalCheck, Strings.Get("approval.auto_label"));
        AutomationProperties.SetHelpText(_autoApprovalCheck, Strings.Get("approval.auto_hint"));
        KeyboardNavigation.SetTabIndex(_autoApprovalCheck, 0);
        _autoApprovalCheck.Checked += OnAutoApprovalChanged;
        _autoApprovalCheck.Unchecked += OnAutoApprovalChanged;
        AutoApprovalOptionsHost.Children.Add(_autoApprovalCheck);
        AutoApprovalOptionsHost.Children.Add(new TextBlock
        {
            Text = Strings.Get("approval.auto_hint"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 4),
            Style = (Style)FindResource("Note"),
        });
    }

    private void SyncAutoApprovalOption(AccountProfile? selected)
    {
        if (_autoApprovalCheck is null) return;
        _syncingAutoApproval = true;
        try
        {
            _autoApprovalCheck.IsEnabled = selected is not null;
            _autoApprovalCheck.IsChecked = selected?.AutoAcceptApprovalCards == true;
        }
        finally { _syncingAutoApproval = false; }
    }

    private void OnAutoApprovalChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingAutoApproval || _autoApprovalCheck is null || SelectedProfile is not { } profile) return;
        var enabled = _autoApprovalCheck.IsChecked == true;
        _syncingAutoApproval = true;
        try
        {
            if (enabled)
            {
                var invoker = FocusReturn.Capture();
                var answer = System.Windows.MessageBox.Show(this,
                    Strings.Get("approval.auto_warning"), Strings.Get("approval.auto_label"),
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                FocusReturn.Restore(invoker);
                enabled = answer == MessageBoxResult.Yes;
            }
            profile.AutoAcceptApprovalCards = enabled;
            _autoApprovalCheck.IsChecked = enabled;
        }
        finally { _syncingAutoApproval = false; }
    }
}

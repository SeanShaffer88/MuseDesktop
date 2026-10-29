using System.Windows;
using MuseDesktop.Core;

namespace MuseDesktop;

/// <summary>
/// The token ring's panel (R7B, v1.8.2): the current reading in
/// words — the counts when the page reported them, the percent
/// always, and the SOURCE stated plainly on its own line (reported
/// by the page, or an estimate with the reason and the rule of
/// thumb spelled out). In estimate mode a third line names the
/// limit the estimate is measured against and where to change it.
/// The reading itself is Core's TokenUsage; this window only words
/// it, through the catalog like every other shell string. Refresh
/// re-probes the page through the main window's delegate and
/// re-renders in place.
/// </summary>
public partial class TokenUsageWindow : Window
{
    private readonly Func<Task<TokenUsage?>> _refresh;
    private readonly int _estimatedLimitTokens;

    public TokenUsageWindow(TokenUsage? usage, int estimatedLimitTokens, Func<Task<TokenUsage?>> refresh)
    {
        InitializeComponent();
        _refresh = refresh;
        _estimatedLimitTokens = estimatedLimitTokens;
        Render(usage);
    }

    private void Render(TokenUsage? usage)
    {
        if (usage is null)
        {
            HeadlineText.Text = Strings.Get("token.no_data");
            SourceText.Text = "";
            SourceText.Visibility = Visibility.Collapsed;
            NoteText.Text = "";
            NoteText.Visibility = Visibility.Collapsed;
            return;
        }
        HeadlineText.Text = usage switch
        {
            { UsedTokens: { } used, LimitTokens: { } limit } => Strings.Format("token.used_of_limit",
                TokenUsageText.FormatCount(used), TokenUsageText.FormatCount(limit), usage.PercentRounded),
            _ => Strings.Format("token.percent_only", usage.PercentRounded),
        };
        SourceText.Text = usage.IsEstimate
            ? Strings.Format("token.source_estimate", TokenEstimator.CharsPerToken)
            : Strings.Get("token.source_page");
        SourceText.Visibility = Visibility.Visible;
        NoteText.Text = usage.IsEstimate
            ? Strings.Format("token.estimate_limit_note", TokenUsageText.FormatCount(_estimatedLimitTokens))
            : "";
        NoteText.Visibility = usage.IsEstimate ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) =>
        Render(await _refresh());
}

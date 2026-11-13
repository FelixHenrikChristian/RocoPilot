using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RocoPilot.Controls;

namespace RocoPilot.Views;

public sealed partial class StatisticsSyncMergeDialog : AppContentDialog
{
    public StatisticsSyncMergeDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateContentBounds();
    }

    private void UpdateContentBounds()
    {
        if (XamlRoot is null) return;
        RulesContent.Width = Math.Min(380, Math.Max(240, XamlRoot.Size.Width - 96));
        RulesScrollViewer.MaxHeight = Math.Min(440, Math.Max(120, XamlRoot.Size.Height - 200));
    }
}

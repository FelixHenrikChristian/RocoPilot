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

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        // WinUI replaces the default button's Style; local values preserve geometry.
        foreach (var name in new[] { "PrimaryButton", "CloseButton" })
        {
            if (GetTemplateChild(name) is not Button button) continue;
            button.FontSize = 13;
            button.MinHeight = 34;
            button.MinWidth = 96;
            button.Padding = new Thickness(14, 6, 14, 6);
            button.CornerRadius = new CornerRadius(8);
        }
    }

    private void UpdateContentBounds()
    {
        if (XamlRoot is null) return;
        RulesContent.Width = Math.Min(380, Math.Max(240, XamlRoot.Size.Width - 96));
        RulesScrollViewer.MaxHeight = Math.Min(440, Math.Max(120, XamlRoot.Size.Height - 200));
    }
}

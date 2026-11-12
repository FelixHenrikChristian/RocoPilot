using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RocoPilot.Controls;

namespace RocoPilot.Views;

public sealed partial class SettingsResetDialog : AppContentDialog
{
    public SettingsResetDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateContentBounds();
    }

    private void UpdateContentBounds()
    {
        if (XamlRoot is null) return;
        ResetContent.Width = Math.Min(360, Math.Max(240, XamlRoot.Size.Width - 96));
        ResetScrollViewer.MaxHeight = Math.Min(440, Math.Max(120, XamlRoot.Size.Height - 200));
    }
}

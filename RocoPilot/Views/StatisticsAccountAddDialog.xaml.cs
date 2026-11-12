using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RocoPilot.Controls;

namespace RocoPilot.Views;

public sealed partial class StatisticsAccountAddDialog : AppContentDialog
{
    public string Uid => UidTextBox.Text;

    public StatisticsAccountAddDialog()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            UpdateContentBounds();
            UidTextBox.Focus(FocusState.Programmatic);
        };
    }

    private void UpdateContentBounds()
    {
        if (XamlRoot is null) return;
        AccountContent.Width = Math.Min(360, Math.Max(240, XamlRoot.Size.Width - 96));
        AccountScrollViewer.MaxHeight = Math.Min(440, Math.Max(120, XamlRoot.Size.Height - 200));
    }
}

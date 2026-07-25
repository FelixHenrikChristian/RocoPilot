using Microsoft.UI.Xaml.Controls;

namespace RocoPilot.Views.Windows.AutoBattleConfigPages;

public sealed partial class AutoBattleBloodlineCaptureConfigPage : Page
{
    internal AutoBattleConfigEditor Editor
    {
        get;
    }

    internal AutoBattleBloodlineCaptureConfigPage(AutoBattleConfigEditor editor)
    {
        Editor = editor;
        InitializeComponent();
    }
}

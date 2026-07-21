using Microsoft.UI.Xaml.Controls;

namespace RocoPilot.Views.Windows.AutoBattleConfigPages;

public sealed partial class AutoBattleLegendaryConfigPage : Page
{
    internal AutoBattleConfigEditor Editor
    {
        get;
    }

    internal AutoBattleLegendaryConfigPage(AutoBattleConfigEditor editor)
    {
        Editor = editor;
        InitializeComponent();
    }
}

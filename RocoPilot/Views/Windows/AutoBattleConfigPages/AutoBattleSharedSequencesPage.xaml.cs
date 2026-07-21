using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RocoPilot.Views.Windows.AutoBattleConfigPages;

public sealed partial class AutoBattleSharedSequencesPage : Page
{
    internal AutoBattleConfigEditor Editor
    {
        get;
    }

    internal AutoBattleSharedSequencesPage(AutoBattleConfigEditor editor)
    {
        Editor = editor;
        InitializeComponent();
    }

    private void AddPresetButton_Click(object sender, RoutedEventArgs e)
    {
        Editor.AddSharedPreset();
    }

    private void DeletePresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AutoBattlePresetEditorItem preset })
        {
            Editor.RemoveSharedPreset(preset);
        }
    }
}

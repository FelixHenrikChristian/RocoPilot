using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace RocoPilot.Views.Windows.AutoBattleConfigPages;

public sealed partial class AutoBattleSpiritCaptureConfigPage : Page
{
    private CancellationTokenSource? _searchCancellation;

    internal AutoBattleConfigEditor Editor
    {
        get;
    }

    internal AutoBattleSpiritCaptureConfigPage(AutoBattleConfigEditor editor)
    {
        Editor = editor;
        InitializeComponent();
        UpdateEmptyHint();
        Editor.CaptureRuleItems.CollectionChanged += (_, _) => UpdateEmptyHint();
    }

    private async void SpiritSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        _searchCancellation?.Cancel();
        var keyword = sender.Text;
        if (string.IsNullOrWhiteSpace(keyword))
        {
            sender.ItemsSource = null;
            return;
        }
        var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        _searchCancellation = cancellation;
        try
        {
            await Task.Delay(250, cancellation.Token);
            var suggestions = await Editor.SearchSpiritsAsync(keyword, cancellation.Token);
            if (!cancellation.IsCancellationRequested) sender.ItemsSource = suggestions;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void SpiritSearchBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is SpiritSearchSuggestionItem item) sender.Text = item.Name;
    }

    private void SpiritSearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is SpiritSearchSuggestionItem chosen)
        {
            AddSpirit(chosen.Name);
            return;
        }
        if (!string.IsNullOrWhiteSpace(args.QueryText)) AddSpirit(args.QueryText.Trim());
    }

    private void AddButton_Click(object sender, RoutedEventArgs e) => AddSpirit(SpiritSearchBox.Text);

    private void AddSpirit(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var isDefeat = NewRuleActionBox.SelectedIndex == 1;
        if (Editor.AddCaptureRule(name, isDefeat))
        {
            SpiritSearchBox.Text = string.Empty;
            SpiritSearchBox.ItemsSource = null;
        }
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: EncounterCaptureRuleEditorItem item })
        {
            Editor.RemoveCaptureRule(item);
        }
    }

    private void UpdateEmptyHint() => EmptyListHint.Visibility = Editor.CaptureRuleItems.Count == 0
        ? Visibility.Visible
        : Visibility.Collapsed;
}

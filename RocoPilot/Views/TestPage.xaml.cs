using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RocoPilot.ViewModels;
using RocoPilot.Views.Test;

namespace RocoPilot.Views;

public sealed partial class TestPage : Page
{
    public TestViewModel ViewModel
    {
        get;
    }

    public TestPage()
    {
        ViewModel = App.GetService<TestViewModel>();
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        NavigateToSelectedItem(TestSelectorBar.SelectedItem ?? ScreenCaptureSelectorItem);
    }

    private void TestSelectorBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is not null)
        {
            NavigateToSelectedItem(sender.SelectedItem);
        }
    }

    private void NavigateToSelectedItem(SelectorBarItem selectedItem)
    {
        var pageType = selectedItem switch
        {
            _ when selectedItem == TextRecognitionSelectorItem => typeof(TextRecognitionTestPage),
            _ when selectedItem == InputSimulationSelectorItem => typeof(InputSimulationTestPage),
            _ => typeof(ScreenCaptureTestPage)
        };

        if (TestContentFrame.CurrentSourcePageType != pageType)
        {
            TestContentFrame.Navigate(pageType);
        }
    }
}

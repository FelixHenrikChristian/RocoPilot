using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RocoPilot.Controls;

/// <summary>Shared presentation for application-owned modal dialogs.</summary>
public class AppContentDialog : ContentDialog
{
    public AppContentDialog()
    {
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("ms-appx:///Styles/DialogControls.xaml")
        });
        Style = (Style)Application.Current.Resources["AppContentDialogStyle"];
    }
}

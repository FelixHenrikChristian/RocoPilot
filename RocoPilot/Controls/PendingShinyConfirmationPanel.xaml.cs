using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace RocoPilot.Controls;

public sealed partial class PendingShinyConfirmationPanel : UserControl
{
    public event RoutedEventHandler? ConfirmRequested;
    public event RoutedEventHandler? DiscardRequested;

    public PendingShinyConfirmationPanel()
    {
        InitializeComponent();
        var name = new TextBox
        {
            Header = "精灵名",
            PlaceholderText = "输入正确的精灵名",
            MaxLength = 32
        };
        name.SetBinding(TextBox.TextProperty, new Binding
        {
            Path = new PropertyPath("PendingEditor.Name"),
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
        });
        NamePreviewHost.Child = SpiritNamePreview.CreateField(name, shiny: true);
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e) => ConfirmRequested?.Invoke(this, e);

    private void DiscardButton_Click(object sender, RoutedEventArgs e) => DiscardRequested?.Invoke(this, e);
}

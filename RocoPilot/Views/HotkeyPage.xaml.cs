using System.Runtime.InteropServices;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

using RocoPilot.Models.Hotkeys;
using RocoPilot.Models.Input;
using RocoPilot.ViewModels;

using Windows.System;

namespace RocoPilot.Views;

public sealed partial class HotkeyPage : Page
{
    public HotkeyViewModel ViewModel
    {
        get;
    }

    public HotkeyPage()
    {
        ViewModel = App.GetService<HotkeyViewModel>();
        InitializeComponent();
        HotkeyTreeView.ItemTemplateSelector = new HotkeyTreeItemTemplateSelector
        {
            GroupTemplate = (DataTemplate)Resources["HotkeyGroupTemplate"],
            BindingTemplate = (DataTemplate)Resources["HotkeyBindingTemplate"]
        };
        InitializeHotkeyTree();
        Loaded += HotkeyPage_Loaded;
    }

    private async void HotkeyPage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= HotkeyPage_Loaded;
        await ViewModel.LoadAsync();
    }

    private void BindingButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: HotkeyBindingItemViewModel item } button)
        {
            return;
        }

        ViewModel.BeginCapture(item);
        button.Focus(FocusState.Programmatic);
    }

    private void InitializeHotkeyTree()
    {
        HotkeyTreeView.RootNodes.Clear();

        foreach (var group in ViewModel.Groups)
        {
            var groupNode = new TreeViewNode
            {
                Content = group,
                IsExpanded = true
            };

            foreach (var item in group.Items)
            {
                groupNode.Children.Add(new TreeViewNode
                {
                    Content = item
                });
            }

            HotkeyTreeView.RootNodes.Add(groupNode);
        }
    }

    private async void HotkeyPage_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!ViewModel.IsCapturing)
        {
            return;
        }

        e.Handled = true;
        if (e.Key == VirtualKey.Escape)
        {
            await ViewModel.ClearCapturingBindingAsync();
            return;
        }

        if (!TryCreateBinding(e.Key, out var binding, out var error))
        {
            if (!string.IsNullOrWhiteSpace(error))
            {
                ViewModel.ShowCaptureError(error);
            }

            return;
        }

        await ViewModel.SetCapturingBindingAsync(binding);
    }

    private static bool TryCreateBinding(
        VirtualKey key,
        out HotkeyBinding binding,
        out string error)
    {
        binding = null!;
        error = string.Empty;

        var virtualKey = (int)key;
        if (!KeyCatalog.TryGetDefinitionByVirtualKey(virtualKey, out var keyDefinition))
        {
            error = "暂不支持这个按键。";
            return false;
        }

        if (keyDefinition.IsModifier)
        {
            return false;
        }

        var modifiers = new List<int>();
        if (IsKeyDown((int)VirtualKey.Control))
        {
            modifiers.Add((int)VirtualKey.Control);
        }

        if (IsKeyDown((int)VirtualKey.Menu))
        {
            modifiers.Add((int)VirtualKey.Menu);
        }

        if (IsKeyDown((int)VirtualKey.Shift))
        {
            modifiers.Add((int)VirtualKey.Shift);
        }

        binding = HotkeyBinding.Create(modifiers, virtualKey);
        return true;
    }

    private static bool IsKeyDown(int virtualKey)
    {
        return (GetKeyState(virtualKey) & 0x8000) != 0;
    }

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    private sealed class HotkeyTreeItemTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? GroupTemplate
        {
            get;
            init;
        }

        public DataTemplate? BindingTemplate
        {
            get;
            init;
        }

        protected override DataTemplate? SelectTemplateCore(object item)
        {
            return item is TreeViewNode { Content: var content }
                ? SelectTemplateForContent(content)
                : SelectTemplateForContent(item);
        }

        protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
        {
            return SelectTemplateCore(item);
        }

        private DataTemplate? SelectTemplateForContent(object? content)
        {
            return content switch
            {
                HotkeyGroupViewModel => GroupTemplate,
                HotkeyBindingItemViewModel => BindingTemplate,
                _ => base.SelectTemplateCore(content ?? string.Empty)
            };
        }
    }
}

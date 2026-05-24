using CommunityToolkit.Mvvm.ComponentModel;

using RocoPilot.Models.Hotkeys;

namespace RocoPilot.ViewModels;

public class HotkeyBindingItemViewModel : ObservableObject
{
    private HotkeyBinding? _binding;
    private bool _isCapturing;

    public HotkeyAction Action
    {
        get;
    }

    public string Name
    {
        get;
    }

    public string Description
    {
        get;
    }

    public string Glyph
    {
        get;
    }

    public HotkeyBinding? Binding
    {
        get => _binding;
        set
        {
            if (SetProperty(ref _binding, value))
            {
                OnPropertyChanged(nameof(DisplayText));
                OnPropertyChanged(nameof(SecondaryText));
            }
        }
    }

    public bool IsCapturing
    {
        get => _isCapturing;
        set
        {
            if (SetProperty(ref _isCapturing, value))
            {
                OnPropertyChanged(nameof(DisplayText));
                OnPropertyChanged(nameof(SecondaryText));
            }
        }
    }

    public string DisplayText => IsCapturing
        ? "按下快捷键..."
        : Binding?.DisplayText ?? "未绑定";

    public string SecondaryText => IsCapturing
        ? "按 Esc 取消并清除当前绑定"
        : Binding is null
            ? "点击后设置热键"
            : "点击后重新设置，Esc 可清除";

    public HotkeyBindingItemViewModel(HotkeyActionDescriptor descriptor)
    {
        Action = descriptor.Action;
        Name = descriptor.Name;
        Description = descriptor.Description;
        Glyph = descriptor.Glyph;
    }
}

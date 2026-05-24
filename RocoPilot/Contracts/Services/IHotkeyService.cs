using RocoPilot.Models.Hotkeys;

namespace RocoPilot.Contracts.Services;

public interface IHotkeyService
{
    event EventHandler? SettingsChanged;

    event EventHandler<HotkeyTriggeredEventArgs>? HotkeyTriggered;

    HotkeySettings Settings
    {
        get;
    }

    Task LoadSettingsAsync(CancellationToken cancellationToken = default);

    Task<HotkeyBindingUpdateResult> SetBindingAsync(
        HotkeyAction action,
        HotkeyBinding? binding,
        CancellationToken cancellationToken = default);
}

using RocoPilot.Models.Hotkeys;

namespace RocoPilot.Services.Hotkeys;

internal sealed record HotkeyHostConfiguration(string GameProcessName, HotkeySettings Settings);

internal sealed class HotkeyMatcher
{
    private readonly object _gate = new();
    private readonly HashSet<int> _pressedKeys = [];
    private HotkeyHostConfiguration _configuration = new(string.Empty, new());

    public void Configure(HotkeyHostConfiguration configuration)
    {
        lock (_gate)
        {
            _configuration = configuration;
            _pressedKeys.Clear();
        }
    }

    public (bool Handled, HotkeyAction? Action) ProcessKey(
        int key, bool isDown, IReadOnlyCollection<int> modifiers, string? foregroundProcessName)
    {
        lock (_gate)
        {
            if (!isDown)
            {
                return (_pressedKeys.Remove(key), null);
            }

            if (_pressedKeys.Contains(key))
            {
                return (true, null);
            }

            if (!string.Equals(foregroundProcessName, _configuration.GameProcessName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return default;
            }

            var assignment = _configuration.Settings.Bindings.FirstOrDefault(item =>
                Enum.IsDefined(item.Action)
                && item.Binding is { } binding
                && binding.Key == key
                && binding.Modifiers.Count == modifiers.Count
                && binding.Modifiers.All(modifiers.Contains));
            if (assignment is null)
            {
                return default;
            }

            _pressedKeys.Add(key);
            return (true, assignment.Action);
        }
    }
}

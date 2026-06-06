using RocoPilot.Models.Input;

namespace RocoPilot.Contracts.Services;

public interface IKeyboardInputService
{
    bool IsWindowAvailable(IntPtr hwnd);

    bool IsWindowForeground(IntPtr hwnd);

    bool RequiresForeground(KeyboardInputMethod method);

    bool TryParseSequence(
        string sequence,
        out IReadOnlyList<KeyStroke> keyStrokes,
        out string error);

    Task SendSequenceAsync(
        IntPtr hwnd,
        string sequence,
        KeyboardInputOptions? options = null,
        CancellationToken cancellationToken = default);

    Task SendSequenceAsync(
        IntPtr hwnd,
        IReadOnlyList<KeyStroke> keyStrokes,
        KeyboardInputOptions? options = null,
        CancellationToken cancellationToken = default);
}

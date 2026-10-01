using RocoPilot.Models.Input;

namespace RocoPilot.Contracts.Services;

public interface IKeyboardInputService
{
    bool IsWindowAvailable(IntPtr hwnd);

    bool IsWindowForeground(IntPtr hwnd);

    bool RequiresForeground(KeyboardInputMethod method);

    /// <summary>检查并准备输入方式所需的驱动和设备，不发送按键；不可用时抛出异常。</summary>
    void EnsureReady(KeyboardInputMethod method);

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

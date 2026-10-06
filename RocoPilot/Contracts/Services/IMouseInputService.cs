namespace RocoPilot.Contracts.Services;

public interface IMouseInputService
{
    Task ClickAsync(
        IntPtr hwnd,
        int x,
        int y,
        KeyboardInputMethod method = KeyboardInputMethod.SendInput,
        CancellationToken cancellationToken = default);

    Task ScrollAsync(
        IntPtr hwnd,
        int x,
        int y,
        int delta,
        KeyboardInputMethod method = KeyboardInputMethod.SendInput,
        CancellationToken cancellationToken = default);

    Task ScrollAtCurrentPositionAsync(
        IntPtr hwnd,
        int delta,
        KeyboardInputMethod method = KeyboardInputMethod.SendInput,
        CancellationToken cancellationToken = default);
}

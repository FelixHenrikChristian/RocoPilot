using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
using RocoPilot.Helpers;
using RocoPilot.Models.Hotkeys;

namespace RocoPilot.Services.Hotkeys;

internal sealed class HotkeyHost
{
    internal const string Argument = "--hotkey-host";
    private const uint WmQuit = 0x0012;
    private static readonly int[] ModifierKeys = [0x11, 0x12, 0x10, 0x5B, 0x5C];
    private readonly HotkeyMatcher _matcher = new();
    private readonly Channel<string> _outgoing = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly LowLevelKeyboardProc _callback;
    private IntPtr _hook;

    private HotkeyHost() => _callback = OnKeyboard;

    internal static int Run(string pipeName, int parentId)
    {
        // 不创建 App/Window。这个线程只泵送键盘消息，管道收发在后台进行。
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            pipe.Connect(10_000);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverId) || serverId != parentId)
            {
                return 1;
            }

            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            var host = new HotkeyHost();
            host.Configure(reader.ReadLine());
            var threadId = GetCurrentThreadId();
            // 确保消息队列已创建，断开连接后才能从管道线程投递 WM_QUIT。
            _ = PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
            host._hook = SetWindowsHookEx(13, host._callback, GetModuleHandle(null), 0);
            if (host._hook == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            using var stopping = new CancellationTokenSource();
            var receive = host.ReceiveAsync(reader, threadId, stopping.Token);
            var send = host.SendAsync(pipe, threadId, stopping.Token);
            host._outgoing.Writer.TryWrite("ready");
            try
            {
                while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
                {
                    _ = TranslateMessage(ref message);
                    _ = DispatchMessage(ref message);
                }
            }
            finally
            {
                _ = UnhookWindowsHookEx(host._hook);
                stopping.Cancel();
                host._outgoing.Writer.TryComplete();
                pipe.Dispose();
                Task.WhenAll(receive, send).GetAwaiter().GetResult();
                GC.KeepAlive(host._callback);
            }

            return 0;
        }
        catch (Exception ex)
        {
            using var logger = LoggingHelper.CreateLogger(LoggingHelper.LogDirectory);
            logger.ForContext<HotkeyHost>().Error(ex, "热键辅助进程 {ProcessId} 异常退出。", Environment.ProcessId);
            return 1;
        }
    }

    internal static void VerifyClient(NamedPipeServerStream pipe, int processId)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientId) || clientId != processId)
        {
            throw new InvalidOperationException("热键辅助进程身份不匹配。");
        }
    }

    internal static ValueTask WriteMessageAsync(PipeStream pipe, string message, CancellationToken cancellationToken) =>
        pipe.WriteAsync(Encoding.UTF8.GetBytes(message + "\n").AsMemory(), cancellationToken);

    private void Configure(string? json)
    {
        var configuration = json is null ? null : JsonSerializer.Deserialize<HotkeyHostConfiguration>(json);
        if (configuration is null || string.IsNullOrWhiteSpace(configuration.GameProcessName)
            || configuration.Settings?.Bindings is null)
        {
            throw new InvalidDataException("热键辅助进程未收到有效配置。");
        }

        _matcher.Configure(configuration);
    }

    private async Task ReceiveAsync(StreamReader reader, uint threadId, CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } json)
            {
                Configure(json);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or JsonException or InvalidDataException)
        {
            Debug.WriteLine(ex);
        }
        finally
        {
            _ = PostThreadMessage(threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private async Task SendAsync(PipeStream pipe, uint threadId, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var message in _outgoing.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await WriteMessageAsync(pipe, message, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            Debug.WriteLine(ex);
        }
        finally
        {
            _ = PostThreadMessage(threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private IntPtr OnKeyboard(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && wParam.ToInt32() is 0x0100 or 0x0101 or 0x0104 or 0x0105)
        {
            var key = Marshal.ReadInt32(lParam);
            var isDown = wParam.ToInt32() is 0x0100 or 0x0104;
            var modifiers = isDown ? ModifierKeys.Where(key => (GetAsyncKeyState(key) & 0x8000) != 0).ToArray() : [];
            var match = _matcher.ProcessKey(key, isDown, modifiers,
                isDown ? GetForegroundProcessName() : null);
            if (match.Action is { } action)
            {
                // 钩子中不执行截图、UI 或管道 I/O，避免 Windows 因回调超时卸载钩子。
                _outgoing.Writer.TryWrite(((int)action).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            if (match.Handled)
            {
                return new IntPtr(1);
            }
        }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static string? GetForegroundProcessName()
    {
        _ = GetWindowThreadProcessId(GetForegroundWindow(), out var processId);
        if (processId == 0) return null;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr Hwnd;
        public uint Id;
        public IntPtr WParam, LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int id, LowLevelKeyboardProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", EntryPoint = "GetMessageW")]
    private static extern int GetMessage(out Message message, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    private static extern bool PeekMessage(out Message message, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW")]
    private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
}

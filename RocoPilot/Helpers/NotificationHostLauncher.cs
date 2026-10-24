using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RocoPilot.Helpers;

internal static class NotificationHostLauncher
{
    internal static void Start(string path, string pipeName, nint parentWindow)
    {
        var windowArgument = parentWindow.ToString("X");
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            using var process = Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { pipeName, windowArgument }
            });
            return;
        }

        // Use the interactive desktop user's token, not the elevated application's token.
        GetWindowThreadProcessId(GetShellWindow(), out var shellProcessId);
        using var shell = OpenProcess(0x1000, false, shellProcessId); // PROCESS_QUERY_LIMITED_INFORMATION
        if (shell.IsInvalid) throw new Win32Exception();
        if (!OpenProcessToken(shell, 0x000A, out var token)) throw new Win32Exception(); // TOKEN_QUERY | TOKEN_DUPLICATE
        using (token)
        {
            if (!DuplicateTokenEx(token, 0x02000000, 0, 2, 1, out var primary)) throw new Win32Exception();
            using (primary)
            {
                var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = @"winsta0\default", Flags = 1 };
                var command = new StringBuilder($"\"{path}\" {pipeName} {windowArgument}");
                if (!CreateProcessWithTokenW(primary, 0, path, command, 0x08000000, 0,
                        Path.GetDirectoryName(path)!, ref startup, out var process)) throw new Win32Exception();
                CloseHandle(process.Thread);
                CloseHandle(process.Process);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public uint X, Y, Width, Height, XChars, YChars, Fill, Flags;
        public ushort ShowWindow, ReservedSize;
        public nint ReservedPointer, StandardInput, StandardOutput, StandardError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { public nint Process, Thread; public uint ProcessId, ThreadId; }

    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(SafeAccessTokenHandle token, uint access, nint attributes, int level, int type, out SafeAccessTokenHandle primary);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessWithTokenW(SafeAccessTokenHandle token, uint logonFlags, string application,
        StringBuilder command, uint flags, nint environment, string directory, ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}

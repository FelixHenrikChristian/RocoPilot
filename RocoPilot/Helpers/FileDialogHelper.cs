using System.Runtime.InteropServices;

using Microsoft.UI.Xaml;

using WinRT.Interop;

namespace RocoPilot.Helpers;

public static class FileDialogHelper
{
    public static string? PickOpenFile(
        string title,
        string filter,
        string? initialDirectory = null)
    {
        const int fileBufferCharCount = 32768;

        var filterBuffer = IntPtr.Zero;
        var fileBuffer = IntPtr.Zero;
        var initialDirectoryBuffer = IntPtr.Zero;
        var titleBuffer = IntPtr.Zero;

        try
        {
            filterBuffer = Marshal.StringToHGlobalUni(BuildWin32Filter(filter));
            fileBuffer = Marshal.AllocHGlobal(fileBufferCharCount * sizeof(char));
            ZeroMemory(fileBuffer, fileBufferCharCount * sizeof(char));
            titleBuffer = Marshal.StringToHGlobalUni(title);

            if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            {
                initialDirectoryBuffer = Marshal.StringToHGlobalUni(initialDirectory);
            }

            var openFileName = new OpenFileName
            {
                lStructSize = Marshal.SizeOf<OpenFileName>(),
                hwndOwner = WindowNative.GetWindowHandle(App.MainWindow),
                lpstrFilter = filterBuffer,
                nFilterIndex = 1,
                lpstrFile = fileBuffer,
                nMaxFile = fileBufferCharCount,
                lpstrInitialDir = initialDirectoryBuffer,
                lpstrTitle = titleBuffer,
                Flags = OpenFileNameFlags.Explorer
                    | OpenFileNameFlags.FileMustExist
                    | OpenFileNameFlags.PathMustExist
                    | OpenFileNameFlags.NoChangeDir
                    | OpenFileNameFlags.HideReadOnly
                    | OpenFileNameFlags.DontAddToRecent
            };

            if (GetOpenFileName(ref openFileName))
            {
                return Marshal.PtrToStringUni(fileBuffer);
            }

            var error = CommDlgExtendedError();
            if (error == 0)
            {
                return null;
            }

            throw new InvalidOperationException($"文件选择窗口返回错误：0x{error:X}");
        }
        finally
        {
            FreeHGlobal(filterBuffer);
            FreeHGlobal(fileBuffer);
            FreeHGlobal(initialDirectoryBuffer);
            FreeHGlobal(titleBuffer);
        }
    }

    public static string? PickSaveFile(
        string title,
        string filter,
        string? initialDirectory = null,
        string? initialFileName = null,
        string? defaultExtension = null,
        Window? owner = null)
    {
        const int fileBufferCharCount = 32768;

        var filterBuffer = IntPtr.Zero;
        var fileBuffer = IntPtr.Zero;
        var initialDirectoryBuffer = IntPtr.Zero;
        var titleBuffer = IntPtr.Zero;
        var defaultExtensionBuffer = IntPtr.Zero;

        try
        {
            filterBuffer = Marshal.StringToHGlobalUni(BuildWin32Filter(filter));
            fileBuffer = Marshal.AllocHGlobal(fileBufferCharCount * sizeof(char));
            ZeroMemory(fileBuffer, fileBufferCharCount * sizeof(char));
            titleBuffer = Marshal.StringToHGlobalUni(title);

            if (!string.IsNullOrWhiteSpace(initialFileName))
            {
                CopyInitialFileName(fileBuffer, fileBufferCharCount, initialFileName);
            }

            if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            {
                initialDirectoryBuffer = Marshal.StringToHGlobalUni(initialDirectory);
            }

            if (!string.IsNullOrWhiteSpace(defaultExtension))
            {
                defaultExtensionBuffer = Marshal.StringToHGlobalUni(defaultExtension.TrimStart('.'));
            }

            var openFileName = new OpenFileName
            {
                lStructSize = Marshal.SizeOf<OpenFileName>(),
                hwndOwner = WindowNative.GetWindowHandle(owner ?? App.MainWindow),
                lpstrFilter = filterBuffer,
                nFilterIndex = 1,
                lpstrFile = fileBuffer,
                nMaxFile = fileBufferCharCount,
                lpstrInitialDir = initialDirectoryBuffer,
                lpstrTitle = titleBuffer,
                lpstrDefExt = defaultExtensionBuffer,
                Flags = OpenFileNameFlags.Explorer
                    | OpenFileNameFlags.PathMustExist
                    | OpenFileNameFlags.NoChangeDir
                    | OpenFileNameFlags.HideReadOnly
                    | OpenFileNameFlags.OverwritePrompt
                    | OpenFileNameFlags.NoReadOnlyReturn
                    | OpenFileNameFlags.DontAddToRecent
            };

            if (GetSaveFileName(ref openFileName))
            {
                return Marshal.PtrToStringUni(fileBuffer);
            }

            var error = CommDlgExtendedError();
            if (error == 0)
            {
                return null;
            }

            throw new InvalidOperationException($"文件保存窗口返回错误：0x{error:X}");
        }
        finally
        {
            FreeHGlobal(filterBuffer);
            FreeHGlobal(fileBuffer);
            FreeHGlobal(initialDirectoryBuffer);
            FreeHGlobal(titleBuffer);
            FreeHGlobal(defaultExtensionBuffer);
        }
    }

    private static void CopyInitialFileName(IntPtr fileBuffer, int fileBufferCharCount, string initialFileName)
    {
        var charCount = Math.Min(initialFileName.Length, fileBufferCharCount - 1);
        var chars = initialFileName.ToCharArray(0, charCount);
        Marshal.Copy(chars, 0, fileBuffer, chars.Length);
    }

    private static string BuildWin32Filter(string filter)
    {
        var parts = filter.Split('|', StringSplitOptions.None);
        if (parts.Length < 2 || parts.Length % 2 != 0)
        {
            throw new ArgumentException("文件过滤器格式无效。", nameof(filter));
        }

        return string.Join('\0', parts) + "\0\0";
    }

    private static void FreeHGlobal(IntPtr buffer)
    {
        if (buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileName(ref OpenFileName openFileName);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSaveFileName(ref OpenFileName openFileName);

    [DllImport("comdlg32.dll")]
    private static extern int CommDlgExtendedError();

    [DllImport("kernel32.dll", EntryPoint = "RtlZeroMemory", SetLastError = false)]
    private static extern void ZeroMemory(IntPtr destination, int length);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public IntPtr lpstrFilter;
        public IntPtr lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public IntPtr lpstrFileTitle;
        public int nMaxFileTitle;
        public IntPtr lpstrInitialDir;
        public IntPtr lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public IntPtr lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public IntPtr lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    private static class OpenFileNameFlags
    {
        public const int HideReadOnly = 0x00000004;
        public const int NoChangeDir = 0x00000008;
        public const int OverwritePrompt = 0x00000002;
        public const int PathMustExist = 0x00000800;
        public const int FileMustExist = 0x00001000;
        public const int Explorer = 0x00080000;
        public const int NoReadOnlyReturn = 0x00008000;
        public const int DontAddToRecent = 0x02000000;
    }
}

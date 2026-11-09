using System.Runtime.InteropServices;
using System.Text;

namespace RocoPilot.Helpers;

/// <summary>
/// Win32 文件打开/保存对话框（comdlg32）。
/// WindowsAppSDK 的 FileOpenPicker/FileSavePicker 在应用以管理员权限运行时会抛 COMException 0x80004005
/// （WindowsAppSDK#2731/#2504，官方建议改用 Win32 对话框）；本工具键盘驱动需要提权运行，故用此替代。
/// </summary>
internal static class Win32FileDialog
{
    private const int MaxPathBuffer = 1024;

    private const int OFN_OVERWRITEPROMPT = 0x0002;
    private const int OFN_HIDEREADONLY = 0x0004;
    private const int OFN_NOCHANGEDIR = 0x0008;
    private const int OFN_PATHMUSTEXIST = 0x0800;
    private const int OFN_FILEMUSTEXIST = 0x1000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENFILENAME
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string? lpstrFilter;
        public IntPtr lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public IntPtr lpstrFileTitle;
        public int nMaxFileTitle;
        public string? lpstrInitialDir;
        public string? lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string? lpstrDefExt;
        public IntPtr lpCustData;
        public IntPtr lpfnHook;
        public string? lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileNameW(ref OPENFILENAME lpofn);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSaveFileNameW(ref OPENFILENAME lpofn);

    /// <summary>打开文件选择对话框；用户取消返回 null。</summary>
    public static string? PickOpenFile(IntPtr ownerHwnd, string filterLabel, string extension)
    {
        var fileBuffer = Marshal.AllocCoTaskMem(MaxPathBuffer * 2);
        try
        {
            Marshal.WriteByte(fileBuffer, 0, 0);
            Marshal.WriteByte(fileBuffer, 1, 0);
            var ofn = BuildDialog(ownerHwnd, filterLabel, extension, fileBuffer);
            ofn.Flags = OFN_PATHMUSTEXIST | OFN_FILEMUSTEXIST | OFN_HIDEREADONLY | OFN_NOCHANGEDIR;
            return GetOpenFileNameW(ref ofn) ? Marshal.PtrToStringUni(fileBuffer) : null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(fileBuffer);
        }
    }

    /// <summary>打开文件保存对话框；用户取消返回 null。</summary>
    public static string? PickSaveFile(IntPtr ownerHwnd, string filterLabel, string extension, string suggestedFileName)
    {
        var fileBuffer = Marshal.AllocCoTaskMem(MaxPathBuffer * 2);
        try
        {
            if (!string.IsNullOrEmpty(suggestedFileName))
            {
                // WriteString 不写 null 终止符，手动补齐避免读取越界。
                var bytes = Encoding.Unicode.GetBytes(suggestedFileName);
                Marshal.Copy(bytes, 0, fileBuffer, bytes.Length);
                Marshal.WriteByte(fileBuffer, bytes.Length, 0);
                Marshal.WriteByte(fileBuffer, bytes.Length + 1, 0);
            }
            else
            {
                Marshal.WriteByte(fileBuffer, 0, 0);
                Marshal.WriteByte(fileBuffer, 1, 0);
            }

            var ofn = BuildDialog(ownerHwnd, filterLabel, extension, fileBuffer);
            ofn.Flags = OFN_OVERWRITEPROMPT | OFN_PATHMUSTEXIST | OFN_HIDEREADONLY | OFN_NOCHANGEDIR;
            return GetSaveFileNameW(ref ofn) ? Marshal.PtrToStringUni(fileBuffer) : null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(fileBuffer);
        }
    }

    private static OPENFILENAME BuildDialog(
        IntPtr ownerHwnd,
        string filterLabel,
        string extension,
        IntPtr fileBuffer)
    {
        var ext = extension.StartsWith('.') ? extension : "." + extension;
        return new OPENFILENAME
        {
            lStructSize = Marshal.SizeOf<OPENFILENAME>(),
            hwndOwner = ownerHwnd,
            lpstrFilter = $"{filterLabel} (*{ext})\0*{ext}\0所有文件 (*.*)\0*.*\0\0",
            nFilterIndex = 1,
            lpstrFile = fileBuffer,
            nMaxFile = MaxPathBuffer,
            lpstrFileTitle = IntPtr.Zero,
            nMaxFileTitle = 0,
            lpstrInitialDir = null,
            lpstrTitle = null,
            lpstrDefExt = ext.TrimStart('.')
        };
    }
}

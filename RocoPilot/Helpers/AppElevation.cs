using System.Security.Principal;

namespace RocoPilot.Helpers;

/// <summary>
/// 当前进程是否以管理员（提升）权限运行。
/// WinUI 3 的拖放实现不支持提升权限的进程，提权后 ListView/GridView 的原生拖拽排序会完全失效，
/// 界面需要降级为点击排序（见 AutoBattleReleaseConfigPage）。
/// </summary>
internal static class AppElevation
{
    private static readonly Lazy<bool> LazyIsElevated = new(Detect);

    public static bool IsElevated => LazyIsElevated.Value;

    private static bool Detect()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            // 查询失败时按普通权限处理，保留原生拖拽。
            return false;
        }
    }
}

using System.Diagnostics;

namespace RocoPilot.Helpers;

public static class ShellLaunchHelper
{
    public static bool OpenFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return false;
        }

        return Process.Start(new ProcessStartInfo
        {
            FileName = folderPath,
            UseShellExecute = true
        }) is not null;
    }

    public static bool LaunchUri(Uri uri)
    {
        return Process.Start(new ProcessStartInfo
        {
            FileName = uri.AbsoluteUri,
            UseShellExecute = true
        }) is not null;
    }
}

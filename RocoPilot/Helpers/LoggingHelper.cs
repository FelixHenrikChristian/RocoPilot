using System;
using System.IO;
using System.Reflection;

using RocoPilot.Services.Logging;

using Serilog;

using Windows.ApplicationModel;

namespace RocoPilot.Helpers;

public static class LoggingHelper
{
    public static string LogDirectory { get; } = ResolveLogDirectory();

    /// <summary>各输出的级别和去向见 <see cref="LogPipeline"/>。</summary>
    internal static Serilog.Core.Logger CreateLogger(
        string directory,
        LogEntryStore? entries = null,
        bool writeDebugFile = true) =>
        LogPipeline.Create(directory, entries, writeDebugFile);

    public static void CloseAndFlush() => Log.CloseAndFlush();

    public static void LogStartup()
    {
        var version = GetAppVersion();
        var isMsix = RuntimeHelper.IsMSIX;
        var os = Environment.OSVersion.VersionString;
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;
        var framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;

        Log.ForContext(typeof(App)).Information("RocoPilot {Version:l} 启动，{Mode:l}，{Arch:l}，{Framework:l}，{OS:l}",
            version, isMsix ? "MSIX" : "Unpackaged", arch, framework, os);
    }

    internal static string FormatLogText(string? text, int maximumLength = 120) =>
        LogText.Compact(text, maximumLength);

    private static string GetAppVersion()
    {
        if (RuntimeHelper.IsMSIX)
        {
            var v = Package.Current.Id.Version;
            return $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
        }

        var asmVer = Assembly.GetExecutingAssembly().GetName().Version;
        return asmVer?.ToString() ?? "0.0.0.0";
    }

    private static string ResolveLogDirectory()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(baseDir, "RocoPilot", "logs");
    }
}

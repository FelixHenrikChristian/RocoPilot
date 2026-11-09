using System;
using System.IO;
using System.Reflection;

using Serilog;
using Serilog.Events;

using Windows.ApplicationModel;

namespace RocoPilot.Helpers;

public static class LoggingHelper
{
    public static string LogDirectory { get; } = ResolveLogDirectory();

    internal static Serilog.Core.Logger CreateLogger(string directory)
    {
        Directory.CreateDirectory(directory);

        const string outputTemplate =
            "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}";

        return new LoggerConfiguration()
            .MinimumLevel.Is(LogEventLevel.Debug)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Debug(outputTemplate: outputTemplate)
            .WriteTo.File(
                path: Path.Combine(directory, "app-.log"),
                restrictedToMinimumLevel: LogEventLevel.Information,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true,
                outputTemplate: outputTemplate)
            .CreateLogger();
    }

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

    internal static string FormatLogText(string? text, int maximumLength = 120)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "<empty>";
        }

        var normalized = string.Join(
            " ",
            text
                .Trim()
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= maximumLength
            ? normalized
            : $"{normalized[..maximumLength]}...";
    }

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

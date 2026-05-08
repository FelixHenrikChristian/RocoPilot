using System;
using System.IO;
using System.Reflection;

using RocoPilot.Services.Logging;

using Serilog;
using Serilog.Events;

using Windows.ApplicationModel;

namespace RocoPilot.Helpers;

public static class LoggingHelper
{
    public static string LogDirectory { get; } = ResolveLogDirectory();

    public static InMemoryLogSink LogBuffer { get; } = new(2000);

    public static void ConfigureSerilog()
    {
        Directory.CreateDirectory(LogDirectory);

        const string outputTemplate =
            "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}";

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(LogEventLevel.Debug)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Debug(outputTemplate: outputTemplate)
            .WriteTo.Sink(LogBuffer)
            .WriteTo.File(
                path: Path.Combine(LogDirectory, "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true,
                outputTemplate: outputTemplate)
            .CreateLogger();
    }

    public static void CloseAndFlush() => Log.CloseAndFlush();

    public static void LogStartupBanner()
    {
        var version = GetAppVersion();
        var isMsix = RuntimeHelper.IsMSIX;
        var os = Environment.OSVersion.VersionString;
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;
        var framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;

        Log.Information("==================== RocoPilot 启动 ====================");
        Log.Information("版本: v{Version:l}  运行模式: {Mode:l}  架构: {Arch}", version, isMsix ? "MSIX" : "Unpackaged", arch);
        Log.Information("运行时: {Framework:l}", framework);
        Log.Information("操作系统: {OS:l}", os);
        Log.Information("日志目录: {LogDirectory:l}", LogDirectory);
    }

    public static void LogShutdown()
    {
        Log.Information("==================== RocoPilot 退出 ====================");
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

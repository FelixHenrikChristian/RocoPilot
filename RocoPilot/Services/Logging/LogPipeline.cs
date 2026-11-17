using Serilog;
using Serilog.Events;

namespace RocoPilot.Services.Logging;

/// <summary>
/// 日志的去向：
/// app-.log 只写 Information 及以上，是给用户看的运行流水；
/// debug-.log 额外包含 Debug，以及出现警告或错误时回放的逐次识别记录（见 <see cref="FrameTraceSink"/>）；
/// 日志页保留 Debug 及以上，默认只显示 Information 及以上。
/// </summary>
internal static class LogPipeline
{
    public const string AppFilePattern = "app-*.log";
    public const string DebugFilePattern = "debug-*.log";

    private const string OutputTemplate =
        "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}";

    // 调试日志增长快，单个文件和总量都要封顶。
    private const long DebugFileSizeLimitBytes = 20L * 1024 * 1024;
    private const int DebugFileCountLimit = 10;
    private static readonly TimeSpan DebugFileTimeLimit = TimeSpan.FromDays(7);

    public static Serilog.Core.Logger Create(
        string directory,
        LogEntryStore? entries,
        bool writeDebugFile)
    {
        Directory.CreateDirectory(directory);

        var configuration = new LoggerConfiguration()
            // 逐次识别记录是 Verbose，要先放行到 FrameTraceSink，由它留在内存里、出问题时再落盘。
            .MinimumLevel.Verbose()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Debug(restrictedToMinimumLevel: LogEventLevel.Debug, outputTemplate: OutputTemplate)
            .WriteTo.File(
                path: Path.Combine(directory, "app-.log"),
                restrictedToMinimumLevel: LogEventLevel.Information,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true,
                outputTemplate: OutputTemplate);

        if (writeDebugFile)
        {
            var debugFile = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.File(
                    path: Path.Combine(directory, "debug-.log"),
                    rollingInterval: RollingInterval.Day,
                    fileSizeLimitBytes: DebugFileSizeLimitBytes,
                    rollOnFileSizeLimit: true,
                    retainedFileCountLimit: DebugFileCountLimit,
                    retainedFileTimeLimit: DebugFileTimeLimit,
                    shared: true,
                    outputTemplate: OutputTemplate)
                .CreateLogger();
            configuration.WriteTo.Sink(new FrameTraceSink(debugFile));
        }

        if (entries is not null)
        {
            configuration.WriteTo.Sink(new LogEntrySink(entries), LogEventLevel.Debug);
        }

        return configuration.CreateLogger();
    }
}

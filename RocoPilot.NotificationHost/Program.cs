using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using CommunityToolkit.WinUI.Notifications;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace RocoPilot.NotificationHost;

internal static class Program
{
    internal const uint NotificationClicked = 0x8000 + 74;

    private static async Task Main(string[] args)
    {
        try { await RunAsync(args); }
        catch (IOException) { } // The parent can exit while an acknowledgement is being flushed.
    }

    private static async Task RunAsync(string[] args)
    {
        // Toast activation after the main application has exited has no running session to return to.
        if (args.Length != 2 || !args[0].StartsWith("RocoPilot.Notifications.", StringComparison.Ordinal)
            || !long.TryParse(args[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var window)) return;

        using var pipe = new NamedPipeClientStream(".", args[0], PipeDirection.InOut, PipeOptions.Asynchronous);
        using var connectionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await pipe.ConnectAsync(connectionTimeout.Token); }
        catch (OperationCanceledException) { return; } // The parent exited before connecting.
        catch (IOException) { return; }
        using var reader = new StreamReader(pipe);
        using var writer = new StreamWriter(pipe) { AutoFlush = true };
        try
        {
            var notifier = ToastNotificationManagerCompat.CreateToastNotifier();
            ToastNotificationManagerCompat.OnActivated += _ => PostMessage((nint)window, NotificationClicked, 0, 0);
            await writer.WriteLineAsync(JsonSerializer.Serialize("ready"));
            while (await reader.ReadLineAsync() is { } payload)
            {
                try
                {
                    if (notifier.Setting != NotificationSetting.Enabled)
                        throw new InvalidOperationException("Windows 通知不可用：" + notifier.Setting);
                    var document = new XmlDocument();
                    document.LoadXml(JsonSerializer.Deserialize<string>(payload)!);
                    notifier.Show(new ToastNotification(document));
                    await writer.WriteLineAsync(JsonSerializer.Serialize("ok"));
                }
                catch (Exception ex)
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(ex.ToString()));
                }
            }
        }
        catch (Exception ex)
        {
            try { await writer.WriteLineAsync(JsonSerializer.Serialize(ex.ToString())); }
            catch (IOException) { } // Parent closed its pipe during shutdown.
        }
    }

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nuint wparam, nint lparam);
}

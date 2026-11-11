using Microsoft.UI.Dispatching;
using RocoPilot.Services.Hotkeys;

namespace RocoPilot;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == HotkeyHost.Argument)
        {
            return args.Length == 3 && int.TryParse(args[2], out var parentId)
                ? HotkeyHost.Run(args[1], parentId)
                : 1;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
        return 0;
    }
}

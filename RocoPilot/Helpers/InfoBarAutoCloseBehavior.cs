using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RocoPilot.Helpers;

public static class InfoBarAutoCloseBehavior
{
    public static readonly DependencyProperty AutoCloseDelayMillisecondsProperty =
        DependencyProperty.RegisterAttached(
            "AutoCloseDelayMilliseconds",
            typeof(int),
            typeof(InfoBarAutoCloseBehavior),
            new PropertyMetadata(0, OnAutoCloseDelayMillisecondsChanged));

    private static readonly DependencyProperty StateProperty =
        DependencyProperty.RegisterAttached(
            "State",
            typeof(AutoCloseState),
            typeof(InfoBarAutoCloseBehavior),
            new PropertyMetadata(null));

    public static int GetAutoCloseDelayMilliseconds(DependencyObject obj)
    {
        return (int)obj.GetValue(AutoCloseDelayMillisecondsProperty);
    }

    public static void SetAutoCloseDelayMilliseconds(DependencyObject obj, int value)
    {
        obj.SetValue(AutoCloseDelayMillisecondsProperty, value);
    }

    private static AutoCloseState? GetState(DependencyObject obj)
    {
        return (AutoCloseState?)obj.GetValue(StateProperty);
    }

    private static void SetState(DependencyObject obj, AutoCloseState? value)
    {
        obj.SetValue(StateProperty, value);
    }

    private static void OnAutoCloseDelayMillisecondsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not InfoBar infoBar)
        {
            return;
        }

        var delayMilliseconds = Math.Max((int)e.NewValue, 0);
        if (delayMilliseconds == 0)
        {
            Detach(infoBar);
            return;
        }

        var state = GetState(infoBar);
        if (state is null)
        {
            state = new AutoCloseState();
            state.IsOpenCallbackToken = infoBar.RegisterPropertyChangedCallback(InfoBar.IsOpenProperty, OnIsOpenChanged);
            infoBar.Unloaded += InfoBar_Unloaded;
            SetState(infoBar, state);
        }

        state.Delay = TimeSpan.FromMilliseconds(delayMilliseconds);
        RestartTimer(infoBar, state);
    }

    private static void OnIsOpenChanged(DependencyObject sender, DependencyProperty dp)
    {
        if (sender is InfoBar infoBar && GetState(infoBar) is { } state)
        {
            RestartTimer(infoBar, state);
        }
    }

    private static void InfoBar_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is InfoBar infoBar && GetState(infoBar) is { } state)
        {
            state.Timer?.Stop();
        }
    }

    private static void RestartTimer(InfoBar infoBar, AutoCloseState state)
    {
        state.Timer?.Stop();

        if (!infoBar.IsOpen || state.Delay <= TimeSpan.Zero)
        {
            return;
        }

        var timer = infoBar.DispatcherQueue.CreateTimer();
        timer.Interval = state.Delay;
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (ReferenceEquals(GetState(infoBar)?.Timer, timer) && infoBar.IsOpen)
            {
                infoBar.IsOpen = false;
            }
        };

        state.Timer = timer;
        timer.Start();
    }

    private static void Detach(InfoBar infoBar)
    {
        var state = GetState(infoBar);
        if (state is null)
        {
            return;
        }

        state.Timer?.Stop();
        infoBar.UnregisterPropertyChangedCallback(InfoBar.IsOpenProperty, state.IsOpenCallbackToken);
        infoBar.Unloaded -= InfoBar_Unloaded;
        SetState(infoBar, null);
    }

    private sealed class AutoCloseState
    {
        public TimeSpan Delay { get; set; }

        public long IsOpenCallbackToken { get; set; }

        public DispatcherQueueTimer? Timer { get; set; }
    }
}

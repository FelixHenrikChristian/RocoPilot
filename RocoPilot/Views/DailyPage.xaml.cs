using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

using RocoPilot.ViewModels;

namespace RocoPilot.Views;

public sealed partial class DailyPage : Page
{
    public DailyViewModel ViewModel { get; } = App.GetService<DailyViewModel>();

    public TasksViewModel Challenges { get; } = App.GetService<TasksViewModel>();

    public DailyPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += (_, _) => ViewModel.NoticeRequested -= ShowNotice;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.NoticeRequested += ShowNotice;
        await Challenges.LoadAsync();
        await ViewModel.LoadAsync();
    }

    private void ShowNotice(object? sender, string message)
    {
        DailyNotice.IsOpen = false;
        DailyNotice.Message = message;
        DailyNotice.IsOpen = true;
    }

    private uint? _dragPointer;
    private FrameworkElement? _dropRow;

    private void Handle_Pressed(object sender, PointerRoutedEventArgs e)
    {
        var handle = (Border)sender;
        if (_dragPointer.HasValue || ViewModel.IsRunning ||
            !e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed || !handle.CapturePointer(e.Pointer)) return;

        _dragPointer = e.Pointer.PointerId;
        handle.Opacity = 0.5;
        e.Handled = true;
    }

    private void Handle_Moved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragPointer != e.Pointer.PointerId) return;
        var row = RowAtPointer(e);
        if (row == _dropRow) return;
        if (_dropRow is not null) _dropRow.Opacity = 1;
        _dropRow = row;
        if (row is not null) row.Opacity = 0.7;
    }

    private FrameworkElement? RowAtPointer(PointerRoutedEventArgs e)
    {
        var y = e.GetCurrentPoint(TaskRows).Position.Y;
        for (var i = 0; i < ViewModel.Items.Count; i++)
        {
            if (TaskRows.ContainerFromIndex(i) is not FrameworkElement row) continue;
            var top = row.TransformToVisual(TaskRows).TransformPoint(default).Y;
            if (y >= top && y < top + row.ActualHeight) return row;
        }

        return null;
    }

    private void Handle_Released(object sender, PointerRoutedEventArgs e)
    {
        if (_dragPointer != e.Pointer.PointerId) return;
        var handle = (Border)sender;
        var item = (DailyTaskItemViewModel)handle.Tag;
        var row = RowAtPointer(e);
        var target = row is null ? -1 : TaskRows.IndexFromContainer(row);
        handle.ReleasePointerCapture(e.Pointer);
        var from = ViewModel.Items.IndexOf(item);
        if (from >= 0 && target >= 0 && target != from) ViewModel.Move(item, target - from);
    }

    private void Handle_CaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_dragPointer != e.Pointer.PointerId) return;
        if (_dropRow is not null) _dropRow.Opacity = 1;
        _dropRow = null;
        _dragPointer = null;
        ((Border)sender).Opacity = 1;
    }
}

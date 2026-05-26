using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Windows.Foundation;

namespace RocoPilot.Controls;

public sealed class BalancedWrapPanel : Panel
{
    public static readonly DependencyProperty ItemWidthProperty =
        DependencyProperty.Register(
            nameof(ItemWidth),
            typeof(double),
            typeof(BalancedWrapPanel),
            new PropertyMetadata(224d, OnLayoutPropertyChanged));

    public static readonly DependencyProperty ItemHeightProperty =
        DependencyProperty.Register(
            nameof(ItemHeight),
            typeof(double),
            typeof(BalancedWrapPanel),
            new PropertyMetadata(224d, OnLayoutPropertyChanged));

    public static readonly DependencyProperty HorizontalInsetProperty =
        DependencyProperty.Register(
            nameof(HorizontalInset),
            typeof(double),
            typeof(BalancedWrapPanel),
            new PropertyMetadata(0d, OnLayoutPropertyChanged));

    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    public double HorizontalInset
    {
        get => (double)GetValue(HorizontalInsetProperty);
        set => SetValue(HorizontalInsetProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var availableWidth = GetAvailableWidth(availableSize.Width);
        var layoutWidth = GetLayoutWidth(availableWidth);
        var columns = GetColumnCount(layoutWidth, Children.Count);
        var rows = columns == 0 ? 0 : (int)Math.Ceiling((double)Children.Count / columns);
        var itemSize = new Size(ItemWidth, ItemHeight);

        foreach (var child in Children)
        {
            child.Measure(itemSize);
        }

        return new Size(availableWidth, rows * ItemHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var availableWidth = GetAvailableWidth(finalSize.Width);
        var horizontalInset = GetHorizontalInset();
        var layoutWidth = GetLayoutWidth(availableWidth);
        var columns = GetColumnCount(layoutWidth, Children.Count);
        if (columns == 0)
        {
            return finalSize;
        }

        var gap = columns > 1
            ? Math.Max(0, (layoutWidth - (columns * ItemWidth)) / (columns - 1))
            : 0;

        for (var index = 0; index < Children.Count; index++)
        {
            var row = index / columns;
            var column = index % columns;
            var x = columns == 1
                ? horizontalInset + Math.Max(0, (layoutWidth - ItemWidth) / 2)
                : horizontalInset + (column * (ItemWidth + gap));
            var y = row * ItemHeight;

            Children[index].Arrange(new Rect(x, y, ItemWidth, ItemHeight));
        }

        return finalSize;
    }

    private static void OnLayoutPropertyChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not BalancedWrapPanel panel)
        {
            return;
        }

        panel.InvalidateMeasure();
        panel.InvalidateArrange();
    }

    private double GetAvailableWidth(double width)
    {
        if (!double.IsFinite(width) || width <= 0)
        {
            return ItemWidth;
        }

        return width;
    }

    private double GetLayoutWidth(double width)
    {
        var horizontalInset = GetHorizontalInset();
        return Math.Max(ItemWidth, width - (horizontalInset * 2));
    }

    private double GetHorizontalInset()
    {
        return double.IsFinite(HorizontalInset)
            ? Math.Max(0, HorizontalInset)
            : 0;
    }

    private int GetColumnCount(double availableWidth, int itemCount)
    {
        if (itemCount <= 0 || ItemWidth <= 0 || ItemHeight <= 0)
        {
            return 0;
        }

        var columns = Math.Max(1, (int)Math.Floor(availableWidth / ItemWidth));
        return Math.Min(columns, itemCount);
    }
}

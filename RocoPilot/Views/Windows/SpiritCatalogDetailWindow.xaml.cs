using System.Collections.ObjectModel;

using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Spirits;

using Windows.Graphics;

namespace RocoPilot.Views.Windows;

public sealed partial class SpiritCatalogDetailWindow : WindowEx
{
    private readonly ISpiritCatalogService _spiritCatalogService;
    private readonly IThemeSelectorService _themeSelectorService;

    public ObservableCollection<SpiritCatalogDisplayItem> Items { get; } = [];

    public string HeaderTitle { get; }

    public string Summary { get; }

    public SpiritCatalogDetailWindow(
        string title,
        string summary,
        IReadOnlyList<SpiritCatalogDisplayItem> items,
        ISpiritCatalogService spiritCatalogService)
    {
        HeaderTitle = title;
        Summary = summary;
        _spiritCatalogService = spiritCatalogService;
        _themeSelectorService = App.GetService<IThemeSelectorService>();

        foreach (var item in items)
        {
            Items.Add(item);
        }

        InitializeComponent();

        ContentRoot.RequestedTheme = _themeSelectorService.Theme;

        Title = title;
        AppWindow.Title = title;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets/WindowIcon.ico"));
        AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;
        AppWindow.Resize(new SizeInt32(760, 720));
    }

    private void VariantButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SpiritCatalogDisplayItem item })
        {
            return;
        }

        var items = item.Variants
            .Select(variant => SpiritCatalogDisplayItem.FromCatalogItem(
                variant,
                _spiritCatalogService,
                [],
                [],
                showVariantButton: false,
                showChainButton: false))
            .ToList();
        var window = new SpiritCatalogDetailWindow(
            $"NO.{item.Id} 变种",
            $"{item.Name} · {items.Count} 个变种",
            items,
            _spiritCatalogService);
        window.Activate();
    }
}

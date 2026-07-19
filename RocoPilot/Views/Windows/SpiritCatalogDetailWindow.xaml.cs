using System.Collections.ObjectModel;

using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Spirits;
using RocoPilot.Helpers;

using Windows.Graphics;

namespace RocoPilot.Views.Windows;

public sealed partial class SpiritCatalogDetailWindow : WindowEx
{
    private readonly ISpiritCatalogService _spiritCatalogService;
    private readonly IThemeSelectorService _themeSelectorService;

    public ObservableCollection<SpiritCatalogDisplayItem> Items { get; } = [];

    public string HeaderTitle { get; }

    public string Summary { get; }

    public string SourceDisplayName { get; }

    public Uri? SourceUri { get; }

    public SpiritCatalogDetailWindow(
        string title,
        string summary,
        IReadOnlyList<SpiritCatalogDisplayItem> items,
        ISpiritCatalogService spiritCatalogService,
        string sourceDisplayName,
        Uri? sourceUri)
    {
        HeaderTitle = title;
        Summary = summary;
        SourceDisplayName = sourceDisplayName;
        SourceUri = sourceUri;
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
                SpiritCatalogWindow.CanShowShiny(variant) ? [variant] : [],
                [],
                showVariantButton: false,
                showShinyButton: true,
                showChainButton: false,
                showFullName: true,
                useShinyAvatar: false))
            .ToList();
        var window = new SpiritCatalogDetailWindow(
            $"NO.{item.Id} 变种",
            $"{item.Name} · {items.Count} 个变种",
            items,
            _spiritCatalogService,
            SourceDisplayName,
            SourceUri);
        WindowPlacementHelper.SetOwner(window, this);
        WindowPlacementHelper.CenterOnParent(window, App.MainWindow);
        window.Activate();
    }

    private void ShinyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SpiritCatalogDisplayItem item })
        {
            return;
        }

        var items = item.ShinyItems
            .Select(shiny => SpiritCatalogDisplayItem.FromCatalogItem(
                shiny,
                _spiritCatalogService,
                [],
                [],
                [],
                showVariantButton: false,
                showShinyButton: false,
                showChainButton: false,
                showFullName: true,
                useShinyAvatar: true))
            .ToList();
        if (items.Count == 0)
        {
            return;
        }

        var window = new SpiritCatalogDetailWindow(
            $"NO.{item.Id} 异色",
            $"{item.Name} · {items.Count} 个异色形态",
            items,
            _spiritCatalogService,
            SourceDisplayName,
            SourceUri);
        WindowPlacementHelper.SetOwner(window, this);
        WindowPlacementHelper.CenterOnParent(window, App.MainWindow);
        window.Activate();
    }
}

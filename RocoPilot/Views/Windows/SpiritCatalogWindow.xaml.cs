using System.Collections.ObjectModel;

using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Spirits;
using RocoPilot.Helpers;
using RocoPilot.Models.Spirits;

using Windows.Graphics;

namespace RocoPilot.Views.Windows;

public sealed partial class SpiritCatalogWindow : WindowEx
{
    private readonly ISpiritCatalogService _spiritCatalogService;
    private readonly IThemeSelectorService _themeSelectorService;
    private Dictionary<string, List<SpiritCatalogItem>> _variantsById = new(StringComparer.OrdinalIgnoreCase);
    private string? _sourceId;
    private bool _isLoaded;

    public ObservableCollection<SpiritCatalogDisplayItem> Items { get; } = [];

    public string Summary { get; private set; } = "正在加载图鉴数据";

    public string UpdatedAtDisplay { get; private set; } = string.Empty;

    public string SourceDisplayName { get; private set; } = string.Empty;

    public Uri? SourceUri { get; private set; }

    public SpiritCatalogWindow(string? sourceId = null)
    {
        _spiritCatalogService = App.GetService<ISpiritCatalogService>();
        _themeSelectorService = App.GetService<IThemeSelectorService>();
        _sourceId = string.IsNullOrWhiteSpace(sourceId) ? null : sourceId;

        InitializeComponent();

        ContentRoot.RequestedTheme = _themeSelectorService.Theme;

        Title = "精灵图鉴数据";
        AppWindow.Title = Title;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets/WindowIcon.ico"));
        AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;
        AppWindow.Resize(new SizeInt32(760, 720));
    }

    private async void ContentRoot_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isLoaded)
        {
            return;
        }

        _isLoaded = true;
        await ReloadAsync();
    }

    public async Task ReloadAsync()
    {
        try
        {
            var document = string.IsNullOrWhiteSpace(_sourceId)
                ? await _spiritCatalogService.LoadAsync()
                : await _spiritCatalogService.LoadAsync(_sourceId);
            _variantsById = document.Spirits
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => SortVariants(group).ToList(), StringComparer.OrdinalIgnoreCase);

            Items.Clear();
            foreach (var variants in _variantsById.Values.OrderBy(group => ParseId(group[0].Id)))
            {
                var representative = ChooseRepresentative(variants);
                if (representative is null)
                {
                    continue;
                }

                Items.Add(CreateDisplayItem(
                    representative,
                    variants,
                    GetShinyVariants(representative),
                    BuildEvolutionChainRepresentatives(representative),
                    showVariantButton: true,
                    showShinyButton: true,
                    showChainButton: true,
                    showFullName: false,
                    useShinyAvatar: false));
            }

            Summary = BuildSummary(document);
            UpdatedAtDisplay = BuildUpdatedAtDisplay(document);
            SourceDisplayName = document.Source.Name;
            SourceUri = Uri.TryCreate(document.Source.ListUrl, UriKind.Absolute, out var sourceUri)
                ? sourceUri
                : null;
            EmptyState.Visibility = Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Summary = $"图鉴数据加载失败：{ex.Message}";
            UpdatedAtDisplay = string.Empty;
            SourceDisplayName = string.Empty;
            SourceUri = null;
            EmptyState.Visibility = Visibility.Visible;
        }

        Bindings.Update();
    }

    public async Task SetSourceAsync(string? sourceId)
    {
        var normalizedSourceId = string.IsNullOrWhiteSpace(sourceId) ? null : sourceId;
        if (string.Equals(_sourceId, normalizedSourceId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _sourceId = normalizedSourceId;
        if (_isLoaded)
        {
            await ReloadAsync();
        }
    }

    private void VariantButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SpiritCatalogDisplayItem item })
        {
            return;
        }

        var items = item.Variants
            .Select(variant => CreateDisplayItem(
                variant,
                [],
                CanShowShiny(variant) ? [variant] : [],
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
            .Select(shiny => CreateDisplayItem(
                shiny,
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

    private void EvolutionChainButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SpiritCatalogDisplayItem item })
        {
            return;
        }

        var items = item.ChainItems
            .Select(chainItem => CreateDisplayItem(
                chainItem,
                GetVariants(chainItem.Id),
                GetShinyVariants(chainItem),
                [],
                showVariantButton: true,
                showShinyButton: true,
                showChainButton: false,
                showFullName: false,
                useShinyAvatar: false))
            .ToList();
        var window = new SpiritCatalogDetailWindow(
            $"进化链 - {item.Name}",
            string.Join(" -> ", items.Select(chainItem => chainItem.IdDisplay)),
            items,
            _spiritCatalogService,
            SourceDisplayName,
            SourceUri);
        WindowPlacementHelper.SetOwner(window, this);
        WindowPlacementHelper.CenterOnParent(window, App.MainWindow);
        window.Activate();
    }

    private SpiritCatalogDisplayItem CreateDisplayItem(
        SpiritCatalogItem item,
        IReadOnlyList<SpiritCatalogItem> variants,
        IReadOnlyList<SpiritCatalogItem> shinyItems,
        IReadOnlyList<SpiritCatalogItem> chainItems,
        bool showVariantButton,
        bool showShinyButton,
        bool showChainButton,
        bool showFullName,
        bool useShinyAvatar)
    {
        return SpiritCatalogDisplayItem.FromCatalogItem(
            item,
            _spiritCatalogService,
            variants,
            shinyItems,
            chainItems,
            showVariantButton,
            showShinyButton,
            showChainButton,
            showFullName,
            useShinyAvatar);
    }

    private List<SpiritCatalogItem> BuildEvolutionChainRepresentatives(SpiritCatalogItem item)
    {
        var ids = (item.EvolutionChain.Count == 0
                ? [item.Id]
                : item.EvolutionChain.Select(member => member.Id))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(ParseId)
            .ToList();

        return ids
            .Select(id => ChooseRepresentative(GetVariants(id)))
            .Where(spirit => spirit is not null)
            .Cast<SpiritCatalogItem>()
            .ToList();
    }

    private IReadOnlyList<SpiritCatalogItem> GetVariants(string id)
    {
        return _variantsById.TryGetValue(id, out var variants) ? variants : [];
    }

    private IReadOnlyList<SpiritCatalogItem> GetShinyVariants(SpiritCatalogItem item)
    {
        return FilterShinyVariants(item, GetVariants(item.Id));
    }

    internal static IReadOnlyList<SpiritCatalogItem> FilterShinyVariants(
        SpiritCatalogItem item,
        IEnumerable<SpiritCatalogItem> variants)
    {
        return variants
            .Where(variant =>
                string.Equals(variant.Id, item.Id, StringComparison.OrdinalIgnoreCase)
                && string.Equals(variant.Stage, item.Stage, StringComparison.Ordinal)
                && CanShowShiny(variant))
            .ToList();
    }

    internal static bool CanShowShiny(SpiritCatalogItem item)
    {
        return item.HasShiny
            && !string.IsNullOrWhiteSpace(item.ShinyAvatarPath)
            && !string.Equals(item.Stage, "首领形态", StringComparison.Ordinal)
            && !string.Equals(item.Form, "首领形态", StringComparison.Ordinal);
    }

    internal static SpiritCatalogItem? ChooseRepresentative(IReadOnlyList<SpiritCatalogItem> variants)
    {
        return variants
            .OrderBy(item => string.Equals(item.Form, "原始形态", StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(item => IsDefaultRegionalForm(item.RegionalForm) ? 0 : 1)
            .ThenBy(item => item.Name.Length)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    internal static IEnumerable<SpiritCatalogItem> SortVariants(IEnumerable<SpiritCatalogItem> variants)
    {
        var list = variants.ToList();
        var representative = ChooseRepresentative(list);
        return list
            .OrderBy(item => ReferenceEquals(item, representative) ? 0 : 1)
            .ThenBy(item => item.Name, StringComparer.Ordinal);
    }

    private static bool IsDefaultRegionalForm(string? regionalForm)
    {
        return string.IsNullOrWhiteSpace(regionalForm)
            || string.Equals(regionalForm.Trim(), "本来的样子", StringComparison.Ordinal);
    }

    private static string BuildSummary(SpiritCatalogDocument document)
    {
        return $"{document.Count} 个图鉴编号";
    }

    private static string BuildUpdatedAtDisplay(SpiritCatalogDocument document)
    {
        return document.Source.ScrapedAt == default
            ? "更新时间：未同步"
            : $"更新时间：{document.Source.ScrapedAt.ToLocalTime():yyyy-MM-dd HH:mm}";
    }

    internal static int ParseId(string id)
    {
        return int.TryParse(id, out var value) ? value : int.MaxValue;
    }
}

public sealed class SpiritCatalogDisplayItem
{
    public string Id { get; private init; } = string.Empty;

    public string IdDisplay => $"NO.{Id}";

    public string Name { get; private init; } = string.Empty;

    public BitmapImage? Avatar { get; private init; }

    public IReadOnlyList<SpiritCatalogItem> Variants { get; private init; } = [];

    public IReadOnlyList<SpiritCatalogItem> ShinyItems { get; private init; } = [];

    public IReadOnlyList<SpiritCatalogItem> ChainItems { get; private init; } = [];

    public Visibility VariantButtonVisibility { get; private init; }

    public Visibility ShinyButtonVisibility { get; private init; }

    public Visibility ChainButtonVisibility { get; private init; }

    public static SpiritCatalogDisplayItem FromCatalogItem(
        SpiritCatalogItem item,
        ISpiritCatalogService spiritCatalogService,
        IReadOnlyList<SpiritCatalogItem> variants,
        IReadOnlyList<SpiritCatalogItem> shinyItems,
        IReadOnlyList<SpiritCatalogItem> chainItems,
        bool showVariantButton,
        bool showShinyButton,
        bool showChainButton,
        bool showFullName,
        bool useShinyAvatar)
    {
        return new SpiritCatalogDisplayItem
        {
            Id = item.Id,
            Name = showFullName
                ? item.Name
                : RocoPilot.Helpers.TextMatchingHelper.NormalizeSpiritNameForDisplay(item.Name),
            Avatar = CreateAvatar(spiritCatalogService.ResolveAvatarPath(
                useShinyAvatar ? item.ShinyAvatarPath : item.AvatarPath)),
            Variants = variants,
            ShinyItems = shinyItems,
            ChainItems = chainItems,
            VariantButtonVisibility = showVariantButton && variants.Count > 1
                ? Visibility.Visible
                : Visibility.Collapsed,
            ShinyButtonVisibility = showShinyButton && shinyItems.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed,
            ChainButtonVisibility = showChainButton
                ? Visibility.Visible
                : Visibility.Collapsed
        };
    }

    private static BitmapImage? CreateAvatar(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        return new BitmapImage(new Uri(path, UriKind.Absolute));
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

using RocoPilot.Contracts.Services.Spirits;
using RocoPilot.Models.Spirits;

namespace RocoPilot.Views.Windows.AutoBattleConfigPages;

public sealed partial class AutoBattleBloodlineCaptureConfigPage : Page
{
    private const string CatalogHint = "点一下把整条进化链加入名单，再点一下移出。";
    private const string CatalogFailed = "图鉴加载失败，关闭后重新打开可重试。";

    // 一张卡片一行最多放 6 个头像，再多就把最后一格换成“+N”。
    private const int MaxTileAvatars = 6;

    private readonly Dictionary<string, ImageSource?> _avatars = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<SpiritCatalogItem>? _catalog;
    private Task<bool>? _catalogTask;
    private List<SpiritChainOption>? _options;

    internal AutoBattleConfigEditor Editor
    {
        get;
    }

    internal AutoBattleBloodlineCaptureConfigPage(AutoBattleConfigEditor editor)
    {
        Editor = editor;
        InitializeComponent();
        ListModeBox.SelectedIndex = (int)Editor.SpiritCaptureFilter.Mode;
        RefreshList();
    }

    private void ListModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ListModeBox.SelectedIndex < 0) return;
        Editor.SpiritCaptureFilter.Mode = (SpiritCaptureListMode)ListModeBox.SelectedIndex;
        RefreshList();
    }

    private void RefreshList()
    {
        var filter = Editor.SpiritCaptureFilter;
        var chains = filter.ActiveChains;
        var off = filter.Mode == SpiritCaptureListMode.Off;
        var blacklist = filter.Mode == SpiritCaptureListMode.Blacklist;

        ListSummary.Text = filter.Mode switch
        {
            SpiritCaptureListMode.Blacklist when chains.Count > 0 => $"黑名单：不捕捉名单内的 {chains.Count} 条进化链",
            SpiritCaptureListMode.Blacklist => "黑名单为空：不会跳过任何精灵",
            SpiritCaptureListMode.Whitelist when chains.Count > 0 => $"白名单：只捕捉名单内的 {chains.Count} 条进化链",
            SpiritCaptureListMode.Whitelist => "白名单为空：所有精灵都不会捕捉",
            _ => "已关闭：捕捉时不按精灵筛选"
        };
        ListOffHint.Visibility = off ? Visibility.Visible : Visibility.Collapsed;
        ListOptions.Visibility = off ? Visibility.Collapsed : Visibility.Visible;
        if (off) return;

        ListRule.Text = blacklist
            ? "名单内的精灵连同整条进化链都不捕捉，其他精灵按血脉设置处理。"
            : "只捕捉名单内的精灵（整条进化链），并且还要符合血脉设置。";
        ListEmptyText.Text = blacklist
            ? "黑名单还是空的，点「添加精灵」从图鉴里选"
            : "白名单还是空的，现在所有精灵都不会捕捉";
        ListEmpty.Visibility = chains.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectedChains.Visibility = chains.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        SelectedChains.ItemsSource = chains.Select(CreateTile).ToList();
        // 名单为空时这个开关不起作用，连同说明文字一起变淡。
        UnknownNameOption.IsEnabled = chains.Count > 0;
        UnknownNameText.Opacity = chains.Count > 0 ? 1 : 0.5;

        // 头像要等图鉴：先按名字显示，图鉴到了再刷新一次。
        if (_catalog is null && chains.Count > 0) _ = LoadAvatarsAsync();
    }

    private async Task LoadAvatarsAsync()
    {
        if (await EnsureCatalogAsync()) RefreshList();
    }

    private SpiritChainTile CreateTile(SpiritCaptureChain chain)
    {
        var forms = SpiritChainForms.Resolve(chain, _catalog);
        var shown = forms.Count > MaxTileAvatars ? MaxTileAvatars - 1 : forms.Count;
        var avatars = forms.Take(shown)
            .Select(form => new SpiritFormAvatar(form.Name, LoadAvatar(form.AvatarPath)))
            .ToList();
        if (shown < forms.Count)
        {
            avatars.Add(new SpiritFormAvatar(
                string.Join("、", forms.Skip(shown).Select(form => form.Name)), null, $"+{forms.Count - shown}"));
        }

        return new SpiritChainTile(chain, SpiritChainForms.Title(forms), SpiritChainForms.Detail(forms), avatars);
    }

    private ImageSource? LoadAvatar(string avatarPath)
    {
        if (string.IsNullOrWhiteSpace(avatarPath)) return null;
        if (_avatars.TryGetValue(avatarPath, out var cached)) return cached;

        ImageSource? image = null;
        try
        {
            var path = App.GetService<ISpiritCatalogService>().ResolveAvatarPath(avatarPath);
            // 原图 180px，这里最大显示 32px，按 2 倍缩放解码就够了。
            if (path is not null) image = new BitmapImage { DecodePixelWidth = 72, UriSource = new Uri(path, UriKind.Absolute) };
        }
        catch (Exception ex) when (ex is UriFormatException or IOException or UnauthorizedAccessException)
        {
            // 头像只是装饰，读不到就显示占位图标。
        }

        _avatars[avatarPath] = image;
        return image;
    }

    private async Task<bool> EnsureCatalogAsync()
    {
        if (_catalog is not null) return true;
        var task = _catalogTask ??= LoadCatalogAsync();
        if (await task) return true;
        // 失败后放开，下次打开选择器时重试。
        if (ReferenceEquals(_catalogTask, task)) _catalogTask = null;
        return false;
    }

    private async Task<bool> LoadCatalogAsync()
    {
        try
        {
            _catalog = (await App.GetService<ISpiritCatalogService>().LoadAsync()).Spirits;
            return true;
        }
        catch (Exception ex)
        {
            Serilog.Log.ForContext<AutoBattleBloodlineCaptureConfigPage>()
                .Warning(ex, "战斗配置：精灵图鉴加载失败，捕捉名单暂时只显示名字、不能添加，重新打开选择器会重试");
            return false;
        }
    }

    private async void CatalogFlyout_Opened(object sender, object e)
    {
        CatalogSearch.Text = string.Empty;
        try
        {
            if (_catalog is null)
            {
                CatalogResults.ItemsSource = null;
                CatalogStatus.Text = "正在加载图鉴…";
            }

            if (!await EnsureCatalogAsync())
            {
                CatalogStatus.Text = CatalogFailed;
                return;
            }

            _options ??= BuildOptions(_catalog!);
            var listed = Editor.SpiritCaptureFilter.ActiveChains.Select(chain => chain.Id).ToHashSet();
            foreach (var option in _options) option.IsListed = listed.Contains(option.ChainId);
            FilterCatalog();
            CatalogSearch.Focus(FocusState.Programmatic);
        }
        catch (Exception ex)
        {
            Serilog.Log.ForContext<AutoBattleBloodlineCaptureConfigPage>()
                .Warning(ex, "战斗配置：整理图鉴里的进化链时出错，这次没能列出可添加的精灵");
            CatalogStatus.Text = CatalogFailed;
        }
    }

    private List<SpiritChainOption> BuildOptions(IReadOnlyList<SpiritCatalogItem> catalog) =>
        catalog.GroupBy(AutoBattleConfigEditor.GetSpiritChainId)
            .Select(group =>
            {
                var forms = SpiritChainForms.FromCatalog(group);
                return new SpiritChainOption(
                    group.Key,
                    group.First(),
                    forms,
                    group.SelectMany(item => (item.Aliases ?? []).Append(item.Name).Append(item.WikiName)),
                    LoadAvatar(forms.Count == 0 ? string.Empty : forms[0].AvatarPath));
            })
            .ToList();

    private void CatalogSearch_TextChanged(object sender, TextChangedEventArgs e) => FilterCatalog();

    private void FilterCatalog()
    {
        if (_options is null) return;
        var query = CatalogSearch.Text.Trim();
        var results = _options.Where(option => option.Matches(query)).ToList();
        CatalogResults.ItemsSource = results;
        CatalogStatus.Text = results.Count == 0 ? "没有找到精灵" : CatalogHint;
    }

    private void CatalogResults_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (_catalog is null || e.ClickedItem is not SpiritChainOption option) return;
        var existing = Editor.SpiritCaptureFilter.ActiveChains.FirstOrDefault(chain => chain.Id == option.ChainId);
        if (existing is null) Editor.AddSpiritChain(option.Source, _catalog);
        else Editor.RemoveSpiritChain(existing);
        option.IsListed = existing is null;
        RefreshList();
    }

    private void RemoveChain_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SpiritCaptureChain chain }) return;
        Editor.RemoveSpiritChain(chain);
        RefreshList();
    }
}

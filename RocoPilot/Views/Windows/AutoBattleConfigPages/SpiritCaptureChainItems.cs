using CommunityToolkit.Mvvm.ComponentModel;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

using RocoPilot.Helpers;
using RocoPilot.Models.Spirits;
using RocoPilot.Services.Spirits;

namespace RocoPilot.Views.Windows.AutoBattleConfigPages;

/// <summary>进化链里的一个形态。名单里存的是各形态的原名，界面上同名的季节、分支形态只显示一个。</summary>
internal sealed record SpiritChainForm(string Name, string AvatarPath);

internal static class SpiritChainForms
{
    public static IReadOnlyList<SpiritChainForm> FromCatalog(IEnumerable<SpiritCatalogItem> members)
    {
        var items = members.ToList();
        // 图鉴按编号排，首领形态可能排在初始形态前面；进化链名单才是从初始形态开始的顺序。
        var order = items.FirstOrDefault()?.EvolutionChainNames ?? [];
        return items.OrderBy(Position)
            .Select(item => new SpiritChainForm(SpiritCatalogIndex.BuildDisplayName(item), item.AvatarPath ?? string.Empty))
            .Where(form => form.Name.Length > 0)
            .DistinctBy(form => form.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int Position(SpiritCatalogItem item)
        {
            var index = order.IndexOf(item.Name);
            return index < 0 ? int.MaxValue : index;
        }
    }

    /// <summary>只展示名单里实际存着的名字，图鉴仅用来补头像和排序。</summary>
    public static IReadOnlyList<SpiritChainForm> Resolve(SpiritCaptureChain chain, IReadOnlyList<SpiritCatalogItem>? catalog)
    {
        var names = chain.Names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<SpiritChainForm> forms = catalog is null
            ? []
            : FromCatalog(catalog.Where(item => names.Contains(item.Name))).ToList();

        // 图鉴里找不到的名字（图鉴还没加载完，或同步后改了名）照原样列出，只是没有头像。
        var shown = forms.Select(form => form.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in chain.Names.Select(TextMatchingHelper.NormalizeSpiritNameForDisplay))
        {
            if (name.Length > 0 && shown.Add(name)) forms.Add(new SpiritChainForm(name, string.Empty));
        }

        return forms;
    }

    public static string Title(IReadOnlyList<SpiritChainForm> forms) => forms.Count == 0 ? "未知精灵" : forms[0].Name;

    public static string Detail(IReadOnlyList<SpiritChainForm> forms) => forms.Count <= 1
        ? "无其他形态"
        : string.Join("、", forms.Skip(1).Select(form => form.Name));
}

/// <summary>卡片上的一个头像格。头像放不下时最后一格显示“+N”。</summary>
public sealed class SpiritFormAvatar(string name, ImageSource? image, string overflowText = "")
{
    public string Name { get; } = name;

    public ImageSource? Image { get; } = image;

    public string OverflowText { get; } = overflowText;

    public Visibility PlaceholderVisibility => Image is null && OverflowText.Length == 0
        ? Visibility.Visible
        : Visibility.Collapsed;
}

/// <summary>名单里的一条进化链。</summary>
public sealed class SpiritChainTile(
    SpiritCaptureChain chain, string title, string detail, IReadOnlyList<SpiritFormAvatar> forms)
{
    public SpiritCaptureChain Chain { get; } = chain;

    public string Title { get; } = title;

    public string Detail { get; } = detail;

    public IReadOnlyList<SpiritFormAvatar> Forms { get; } = forms;

    public string RemoveLabel => $"把{Title}移出名单";
}

/// <summary>图鉴选择器里的一条进化链，点一下加入名单，再点一下移出。</summary>
public sealed partial class SpiritChainOption : ObservableObject
{
    private readonly string[] _searchNames;
    private bool _isListed;

    internal SpiritChainOption(
        string chainId, SpiritCatalogItem source, IReadOnlyList<SpiritChainForm> forms,
        IEnumerable<string?> searchNames, ImageSource? avatar)
    {
        ChainId = chainId;
        Source = source;
        Title = SpiritChainForms.Title(forms);
        Detail = SpiritChainForms.Detail(forms);
        Avatar = avatar;
        _searchNames = searchNames.OfType<string>().Where(name => name.Trim().Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal string ChainId { get; }

    internal SpiritCatalogItem Source { get; }

    public string Title { get; }

    public string Detail { get; }

    public ImageSource? Avatar { get; }

    public Visibility PlaceholderVisibility => Avatar is null ? Visibility.Visible : Visibility.Collapsed;

    public bool IsListed
    {
        get => _isListed;
        set
        {
            if (SetProperty(ref _isListed, value)) OnPropertyChanged(nameof(ListedVisibility));
        }
    }

    public Visibility ListedVisibility => IsListed ? Visibility.Visible : Visibility.Collapsed;

    internal bool Matches(string query) => query.Length == 0
        || _searchNames.Any(name => name.Contains(query, StringComparison.OrdinalIgnoreCase));
}

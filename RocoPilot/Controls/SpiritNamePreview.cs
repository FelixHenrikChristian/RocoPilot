using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using RocoPilot.Contracts.Services.Spirits;
using RocoPilot.Helpers;

namespace RocoPilot.Controls;

internal static class SpiritNamePreview
{
    public static Grid CreateField(TextBox nameInput, bool shiny = false)
    {
        var service = App.GetService<ISpiritCatalogService>();
        var avatars = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var image = new Image { Width = 44, Height = 44, Stretch = Stretch.Uniform };
        var placeholder = new FontIcon
        {
            Glyph = "\uE77B",
            FontSize = 22,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            FontFamily = Application.Current.Resources["SymbolThemeFontFamily"] as FontFamily
        };
        var preview = new Grid();
        preview.Children.Add(placeholder);
        preview.Children.Add(image);
        var avatar = new Border
        {
            Width = 52,
            Height = 52,
            VerticalAlignment = VerticalAlignment.Bottom,
            CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["ControlFillColorDefaultBrush"],
            Child = preview
        };
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(nameInput, 1);
        row.Children.Add(avatar);
        row.Children.Add(nameInput);

        void UpdatePreview()
        {
            image.Source = null;
            placeholder.Visibility = Visibility.Visible;
            var key = TextMatchingHelper.NormalizeSpiritNameForMatching(nameInput.Text);
            if (key.Length == 0 || !avatars.TryGetValue(key, out var path) ||
                string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                ToolTipService.SetToolTip(avatar, "暂无匹配的精灵图片");
                return;
            }

            image.Source = new BitmapImage(new Uri(path, UriKind.Absolute));
            ToolTipService.SetToolTip(avatar, nameInput.Text.Trim());
        }

        image.ImageOpened += (_, _) => placeholder.Visibility = Visibility.Collapsed;
        image.ImageFailed += (_, _) => placeholder.Visibility = Visibility.Visible;
        nameInput.TextChanged += (_, _) => UpdatePreview();
        var active = false;
        row.Unloaded += (_, _) => active = false;
        row.Loaded += async (_, _) =>
        {
            active = true;
            try
            {
                var catalog = await service.LoadAsync();
                if (!active) return;
                avatars.Clear();
                foreach (var item in catalog.Spirits)
                {
                    var path = service.ResolveAvatarPath(shiny && !string.IsNullOrWhiteSpace(item.ShinyAvatarPath)
                        ? item.ShinyAvatarPath : item.AvatarPath);
                    foreach (var name in new[] { item.Name, item.WikiName }.Concat(item.Aliases))
                    {
                        var key = TextMatchingHelper.NormalizeSpiritNameForMatching(name);
                        if (key.Length > 0) avatars.TryAdd(key, path);
                    }
                }
                UpdatePreview();
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "精灵头像预览加载失败");
            }
        };
        UpdatePreview();
        return row;
    }

}

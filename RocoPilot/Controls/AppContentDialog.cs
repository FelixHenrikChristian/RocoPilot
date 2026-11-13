using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace RocoPilot.Controls;

/// <summary>Semantic tone of a dialog; drives the title icon color.</summary>
public enum AppDialogTone
{
    /// <summary>Neutral action or information (accent color).</summary>
    Accent,
    /// <summary>Something needs attention but nothing is lost.</summary>
    Caution,
    /// <summary>Destructive or irreversible action.</summary>
    Critical,
    /// <summary>An operation completed successfully.</summary>
    Success
}

/// <summary>Shared presentation for application-owned modal dialogs.</summary>
/// <remarks>
/// Set <see cref="TitleGlyph"/> (Segoe Fluent Icons, see <see cref="DialogGlyphs"/>) to show
/// an icon before the title; <see cref="Tone"/> picks its color. Destructive dialogs keep
/// the safe default via <see cref="UseDestructiveButtons"/>.
/// </remarks>
public class AppContentDialog : ContentDialog
{
    public static readonly DependencyProperty TitleGlyphProperty = DependencyProperty.Register(
        nameof(TitleGlyph), typeof(string), typeof(AppContentDialog),
        new PropertyMetadata(null, (d, _) => ((AppContentDialog)d).UpdateTitle()));

    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone), typeof(AppDialogTone), typeof(AppContentDialog),
        new PropertyMetadata(AppDialogTone.Accent, (d, _) => ((AppContentDialog)d).UpdateTitle()));

    private FontIcon? _titleIcon;
    private bool _updatingTitle;

    public AppContentDialog()
    {
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("ms-appx:///Styles/DialogControls.xaml")
        });
        Style = (Style)Application.Current.Resources["AppContentDialogStyle"];
        RegisterPropertyChangedCallback(TitleProperty, (_, _) => UpdateTitle());
    }

    /// <summary>Segoe Fluent Icons glyph shown before the title.</summary>
    public string? TitleGlyph
    {
        get => (string?)GetValue(TitleGlyphProperty);
        set => SetValue(TitleGlyphProperty, value);
    }

    public AppDialogTone Tone
    {
        get => (AppDialogTone)GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    /// <summary>
    /// Safe default for destructive confirmations: the destructive primary button
    /// is plain, while "Cancel" is the accent default button.
    /// </summary>
    public void UseDestructiveButtons()
    {
        PrimaryButtonStyle = (Style)Application.Current.Resources["DialogButtonStyle"];
        CloseButtonStyle = (Style)Application.Current.Resources["DialogPrimaryButtonStyle"];
        DefaultButton = ContentDialogButton.Close;
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        // WinUI swaps the default button's Style in its visual states, which drops
        // the geometry from DialogButtonStyle; local values keep all buttons aligned.
        var radius = (CornerRadius)Application.Current.Resources["ControlCornerRadius"];
        foreach (var name in new[] { "PrimaryButton", "SecondaryButton", "CloseButton" })
        {
            if (GetTemplateChild(name) is not Button button) continue;
            button.FontSize = 13;
            button.MinHeight = 34;
            button.MinWidth = 96;
            button.Padding = new Thickness(14, 6, 14, 6);
            button.CornerRadius = radius;
        }
    }

    private void UpdateTitle()
    {
        if (_updatingTitle) return;
        _updatingTitle = true;
        try
        {
            if (Title is string text)
            {
                if (string.IsNullOrEmpty(TitleGlyph)) return;
                BuildTitleHeader(text);
            }

            if (_titleIcon is null || !ReferenceEquals(Title, _titleIcon.Parent)) return;
            _titleIcon.Glyph = TitleGlyph ?? string.Empty;
            _titleIcon.Visibility = string.IsNullOrEmpty(TitleGlyph) ? Visibility.Collapsed : Visibility.Visible;
            _titleIcon.Style = (Style)Application.Current.Resources[Tone switch
            {
                AppDialogTone.Caution => "DialogTitleIconCautionStyle",
                AppDialogTone.Critical => "DialogTitleIconCriticalStyle",
                AppDialogTone.Success => "DialogTitleIconSuccessStyle",
                _ => "DialogTitleIconAccentStyle"
            }];
        }
        finally
        {
            _updatingTitle = false;
        }
    }

    private void BuildTitleHeader(string text)
    {
        var titleText = new TextBlock
        {
            Text = text,
            Style = (Style)Application.Current.Resources["DialogTitleTextStyle"],
            VerticalAlignment = VerticalAlignment.Center
        };
        _titleIcon = new FontIcon();
        var header = new Grid { ColumnSpacing = 10 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(titleText, 1);
        header.Children.Add(_titleIcon);
        header.Children.Add(titleText);

        // A UIElement title bypasses TitleTemplate, so the header carries its own text style.
        TitleTemplate = null;
        Title = header;
        // Screen readers would otherwise announce the header Grid instead of the title text.
        AutomationProperties.SetName(this, text);
    }
}

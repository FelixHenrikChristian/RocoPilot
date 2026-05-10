using System.Collections.ObjectModel;
using System.Collections.Specialized;

using CommunityToolkit.Mvvm.ComponentModel;

using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

using RocoPilot.Contracts.Services;
using RocoPilot.Models.Runtime;
using RocoPilot.ViewModels;

using Windows.Graphics;

namespace RocoPilot.Views.Windows;

public sealed partial class AutoBattleConfigWindow : WindowEx
{
    private const string SkillPlaceholder = "{skill}";

    private readonly RealtimeViewModel _viewModel;
    private readonly IKeyboardInputService _keyboardInputService;
    private readonly IThemeSelectorService _themeSelectorService;

    public ObservableCollection<AutoBattleReleaseEditorItem> ReleaseItems
    {
        get;
    } = [];

    public ObservableCollection<AutoBattlePresetEditorItem> PresetItems
    {
        get;
    } = [];

    public string HeaderSummary
    {
        get;
        private set;
    } = string.Empty;

    public AutoBattleConfigWindow(RealtimeViewModel viewModel)
    {
        _viewModel = viewModel;
        _keyboardInputService = App.GetService<IKeyboardInputService>();
        _themeSelectorService = App.GetService<IThemeSelectorService>();

        InitializeComponent();

        ContentRoot.RequestedTheme = _themeSelectorService.Theme;

        Title = "自动战斗配置";
        AppWindow.Title = Title;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets/WindowIcon.ico"));
        HideNativeTitleBar();
        AppWindow.Resize(new SizeInt32(900, 620));

        ReleaseItems.CollectionChanged += ReleaseItems_CollectionChanged;
        PresetItems.CollectionChanged += PresetItems_CollectionChanged;

        LoadSettings(_viewModel.AutoBattleSettings);
        UpdateEditorState();
    }

    private void HideNativeTitleBar()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(true, false);
            return;
        }

        var overlappedPresenter = OverlappedPresenter.Create();
        overlappedPresenter.SetBorderAndTitleBar(true, false);
        AppWindow.SetPresenter(overlappedPresenter);
    }

    private void LoadSettings(AutoBattleSettings settings)
    {
        ReleaseItems.Clear();
        var releaseSequence = settings.ReleaseSequence is { Count: > 0 }
            ? settings.ReleaseSequence
            : AutoBattleSettings.CreateDefaultReleaseSequence();
        foreach (var step in releaseSequence)
        {
            ReleaseItems.Add(CreateReleaseEditorItem(step, settings.TurnSequence));
        }

        PresetItems.Clear();
        foreach (var preset in settings.TurnSequencePresets ?? [])
        {
            PresetItems.Add(new AutoBattlePresetEditorItem
            {
                Name = preset.Name,
                Sequence = preset.Sequence
            });
        }
    }

    private static AutoBattleReleaseEditorItem CreateReleaseEditorItem(
        AutoBattleReleaseStep step,
        string turnSequence)
    {
        if (step.IsCustom)
        {
            return AutoBattleReleaseEditorItem.CreateCustom(step.Name, step.Sequence);
        }

        var skillKey = NormalizeSkillKey(step.SkillKey) ?? "1";
        if (!string.IsNullOrWhiteSpace(turnSequence)
            && !string.Equals(turnSequence.Trim(), AutoBattleSettings.DefaultTurnSequence, StringComparison.Ordinal))
        {
            return AutoBattleReleaseEditorItem.CreateCustom(skillKey, ApplyTurnSequence(turnSequence, skillKey));
        }

        return AutoBattleReleaseEditorItem.CreateSkill(skillKey);
    }

    private void AppendSkillButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string skillKey }
            && NormalizeSkillKey(skillKey) is { } normalizedSkillKey)
        {
            ReleaseItems.Add(AutoBattleReleaseEditorItem.CreateSkill(normalizedSkillKey));
        }
    }

    private void ResetReleaseSequenceButton_Click(object sender, RoutedEventArgs e)
    {
        ReleaseItems.Clear();
        foreach (var step in AutoBattleSettings.CreateDefaultReleaseSequence())
        {
            ReleaseItems.Add(AutoBattleReleaseEditorItem.CreateSkill(step.SkillKey));
        }
    }

    private void ClearReleaseSequenceButton_Click(object sender, RoutedEventArgs e)
    {
        ReleaseItems.Clear();
    }

    private void ReleaseSequenceGrid_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        UpdateEditorState();
    }

    private void ReleaseItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AutoBattleReleaseEditorItem item } element)
        {
            return;
        }

        var deleteItem = new MenuFlyoutItem
        {
            Text = "删除"
        };
        deleteItem.Click += (_, _) => ReleaseItems.Remove(item);

        var flyout = new MenuFlyout();
        flyout.Items.Add(deleteItem);
        flyout.ShowAt(element);
        e.Handled = true;
    }

    private void AddPresetButton_Click(object sender, RoutedEventArgs e)
    {
        PresetItems.Add(new AutoBattlePresetEditorItem
        {
            Name = $"序列 {PresetItems.Count + 1}",
            Sequence = "1"
        });
    }

    private void InsertPresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AutoBattlePresetEditorItem preset })
        {
            return;
        }

        var name = preset.Name.Trim();
        var sequence = preset.Sequence.Trim();
        if (!ValidateNamedSequence(name, sequence, "单回合执行序列"))
        {
            return;
        }

        ReleaseItems.Add(AutoBattleReleaseEditorItem.CreateCustom(name, sequence));
    }

    private void DeletePresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AutoBattlePresetEditorItem preset })
        {
            PresetItems.Remove(preset);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (ReleaseItems.Count == 0)
        {
            ShowMessage("释放顺序为空", "请至少保留一个释放动作。", InfoBarSeverity.Warning);
            return;
        }

        var releaseSequence = new List<AutoBattleReleaseStep>();
        foreach (var item in ReleaseItems)
        {
            if (item.IsCustom)
            {
                if (!ValidateNamedSequence(item.Name, item.Sequence, item.DisplayText))
                {
                    return;
                }

                releaseSequence.Add(AutoBattleReleaseStep.CreateCustom(item.Name.Trim(), item.Sequence.Trim()));
                continue;
            }

            releaseSequence.Add(AutoBattleReleaseStep.CreateSkill(item.SkillKey));
        }

        var presets = new List<AutoBattleTurnSequencePreset>();
        foreach (var preset in PresetItems)
        {
            var name = preset.Name.Trim();
            var sequence = preset.Sequence.Trim();
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(sequence))
            {
                continue;
            }

            if (!ValidateNamedSequence(name, sequence, "单回合执行序列"))
            {
                return;
            }

            presets.Add(new AutoBattleTurnSequencePreset
            {
                Name = name,
                Sequence = sequence
            });
        }

        var settings = _viewModel.AutoBattleSettings.Clone();
        settings.RoundOrder = BuildRoundOrder(releaseSequence);
        settings.TurnSequence = AutoBattleSettings.DefaultTurnSequence;
        settings.ReleaseSequence = releaseSequence;
        settings.TurnSequencePresets = presets;

        _viewModel.UpdateAutoBattleSettings(settings);
        Close();
    }

    private bool ValidateNamedSequence(string name, string sequence, string label)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            ShowMessage("名称为空", $"{label}需要填写名称。", InfoBarSeverity.Warning);
            return false;
        }

        if (string.IsNullOrWhiteSpace(sequence))
        {
            ShowMessage("按键序列为空", $"{label}需要填写按键序列。", InfoBarSeverity.Warning);
            return false;
        }

        if (sequence.Contains(SkillPlaceholder, StringComparison.OrdinalIgnoreCase))
        {
            ShowMessage("按键序列无效", "请写入实际按键，例如 1, Space。", InfoBarSeverity.Warning);
            return false;
        }

        if (!_keyboardInputService.TryParseSequence(sequence, out _, out var error))
        {
            ShowMessage("按键序列无效", error, InfoBarSeverity.Warning);
            return false;
        }

        return true;
    }

    private void ReleaseItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateEditorState();
    }

    private void PresetItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateEditorState();
    }

    private void UpdateEditorState()
    {
        RefreshReleaseIndexes();
        ReleaseEmptyState.Visibility = ReleaseItems.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        PresetEmptyState.Visibility = PresetItems.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        HeaderSummary = BuildHeaderSummary();
        Bindings.Update();
    }

    private void RefreshReleaseIndexes()
    {
        for (var index = 0; index < ReleaseItems.Count; index++)
        {
            ReleaseItems[index].Position = index + 1;
        }
    }

    private string BuildHeaderSummary()
    {
        if (ReleaseItems.Count == 0)
        {
            return "未配置释放顺序";
        }

        var preview = string.Join(" → ", ReleaseItems.Take(8).Select(item => item.DisplayText));
        var suffix = ReleaseItems.Count > 8
            ? $"等 {ReleaseItems.Count} 步"
            : $"{ReleaseItems.Count} 步";
        return $"{preview} · {suffix}";
    }

    private static string BuildRoundOrder(IEnumerable<AutoBattleReleaseStep> releaseSequence)
    {
        var skillKeys = releaseSequence
            .Where(step => !step.IsCustom)
            .Select(step => step.SkillKey)
            .Where(skillKey => NormalizeSkillKey(skillKey) is not null)
            .ToArray();

        return skillKeys.Length == 0
            ? AutoBattleSettings.DefaultRoundOrder
            : string.Join(", ", skillKeys);
    }

    private static string ApplyTurnSequence(string turnSequence, string skillKey)
    {
        var normalized = string.IsNullOrWhiteSpace(turnSequence)
            ? AutoBattleSettings.DefaultTurnSequence
            : turnSequence.Trim();
        return normalized.Contains(SkillPlaceholder, StringComparison.OrdinalIgnoreCase)
            ? normalized.Replace(SkillPlaceholder, skillKey, StringComparison.OrdinalIgnoreCase)
            : normalized;
    }

    private static string? NormalizeSkillKey(string? skillKey)
    {
        if (string.IsNullOrWhiteSpace(skillKey))
        {
            return null;
        }

        var normalized = skillKey.Trim().ToUpperInvariant();
        return normalized is "1" or "2" or "3" or "4" or "X"
            ? normalized
            : null;
    }

    private void ShowMessage(string title, string message, InfoBarSeverity severity)
    {
        MessageBar.Title = title;
        MessageBar.Message = message;
        MessageBar.Severity = severity;
        MessageBar.IsOpen = false;
        MessageBar.IsOpen = true;
    }
}

public sealed class AutoBattleReleaseEditorItem : ObservableObject
{
    private int _position;

    public bool IsCustom
    {
        get;
        private init;
    }

    public string SkillKey
    {
        get;
        private init;
    } = string.Empty;

    public string Name
    {
        get;
        private init;
    } = string.Empty;

    public string Sequence
    {
        get;
        private init;
    } = string.Empty;

    public int Position
    {
        get => _position;
        set
        {
            if (SetProperty(ref _position, value))
            {
                OnPropertyChanged(nameof(PositionText));
            }
        }
    }

    public string PositionText => $"#{Position}";

    public string DisplayText => IsCustom ? Name : SkillKey;

    public string DetailText => IsCustom ? Sequence : "技能键";

    public static AutoBattleReleaseEditorItem CreateSkill(string skillKey)
    {
        return new AutoBattleReleaseEditorItem
        {
            IsCustom = false,
            SkillKey = skillKey,
            Name = skillKey,
            Sequence = string.Empty
        };
    }

    public static AutoBattleReleaseEditorItem CreateCustom(string name, string sequence)
    {
        return new AutoBattleReleaseEditorItem
        {
            IsCustom = true,
            SkillKey = string.Empty,
            Name = string.IsNullOrWhiteSpace(name) ? "自定义" : name.Trim(),
            Sequence = sequence.Trim()
        };
    }
}

public sealed class AutoBattlePresetEditorItem : ObservableObject
{
    private string _name = string.Empty;
    private string _sequence = string.Empty;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string Sequence
    {
        get => _sequence;
        set => SetProperty(ref _sequence, value);
    }
}

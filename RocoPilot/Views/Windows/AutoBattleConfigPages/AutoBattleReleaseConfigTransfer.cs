using Newtonsoft.Json;

using RocoPilot.Contracts.Services;
using RocoPilot.Core.Battle;

namespace RocoPilot.Views.Windows.AutoBattleConfigPages;

/// <summary>
/// 战斗配置的导入导出。导出为战斗类型无关的 JSON 文件；导入到哪个战斗类型页面就归属哪个类型。
/// 含跳过步骤的配置导入普通战斗后，跳过步骤仅展示、执行时自动忽略。
/// </summary>
internal static class AutoBattleReleaseConfigTransfer
{
    public const string FormatIdentifier = "rocopilot.battle-config";
    public const int FormatVersion = 1;
    public const string FileExtension = ".json";

    public sealed class ExportData
    {
        public string Format { get; set; } = FormatIdentifier;

        public int Version { get; set; } = FormatVersion;

        public string Name { get; set; } = string.Empty;

        [JsonProperty(ItemTypeNameHandling = TypeNameHandling.None)]
        public List<AutoBattleReleaseStep> Steps { get; set; } = [];
    }

    public static string Export(AutoBattleReleaseConfigEditorItem item)
    {
        var data = new ExportData
        {
            Name = item.Name.Trim(),
            Steps = item.Sequence.Items
                .Select(ToStep)
                .OfType<AutoBattleReleaseStep>()
                .ToList()
        };
        return JsonConvert.SerializeObject(data, Formatting.Indented);
    }

    private static AutoBattleReleaseStep? ToStep(AutoBattleReleaseEditorItem item)
    {
        return item.IsCustom
            ? AutoBattleReleaseStep.CreateCustom(item.Name.Trim(), item.Sequence.Trim(), item.PresetId)
            : AutoBattleReleaseStep.CreateSkill(item.SkillKey);
    }

    public static bool TryImport(
        string json,
        IKeyboardInputService keyboardInputService,
        out AutoBattleReleaseConfig config,
        out string error)
    {
        config = new AutoBattleReleaseConfig();
        error = string.Empty;

        ExportData? data;
        try
        {
            data = JsonConvert.DeserializeObject<ExportData>(json);
        }
        catch (Exception ex)
        {
            error = $"文件不是有效的战斗配置：{ex.Message}";
            return false;
        }

        if (data is null)
        {
            error = "文件内容为空或格式不正确。";
            return false;
        }

        if (!string.Equals(data.Format, FormatIdentifier, StringComparison.OrdinalIgnoreCase))
        {
            error = "文件不是 RocoPilot 战斗配置（format 标识不匹配）。";
            return false;
        }

        var steps = (data.Steps ?? []).OfType<AutoBattleReleaseStep>().ToList();
        if (steps.Count == 0)
        {
            error = "配置中没有可用步骤。";
            return false;
        }

        foreach (var step in steps)
        {
            if (step.IsCustom)
            {
                var sequence = step.Sequence?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(sequence))
                {
                    error = $"公共序列步骤「{step.Name}」缺少按键序列。";
                    return false;
                }

                if (!keyboardInputService.TryParseSequence(sequence, out _, out var parseError))
                {
                    error = $"公共序列步骤「{step.Name}」按键序列无效：{parseError}";
                    return false;
                }

                step.Sequence = sequence;
                step.Name = string.IsNullOrWhiteSpace(step.Name) ? "公共序列" : step.Name.Trim();
                continue;
            }

            var skillKey = AutoBattleSettingsRules.NormalizeSkillKey(step.SkillKey);
            if (skillKey is null)
            {
                error = $"技能步骤「{step.SkillKey}」无效，仅支持 1/2/3/4/X/跳过。";
                return false;
            }

            step.SkillKey = skillKey;
            step.Name = skillKey;
            step.Sequence = string.Empty;
        }

        config = new AutoBattleReleaseConfig
        {
            Name = string.IsNullOrWhiteSpace(data.Name) ? string.Empty : data.Name.Trim(),
            Steps = steps
        };
        return true;
    }
}

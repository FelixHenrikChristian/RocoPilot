using Microsoft.Extensions.Logging;

using RocoPilot.Configuration;
using RocoPilot.Contracts.Services.Encounters;
using RocoPilot.Contracts.Services.ImageMatching;
using RocoPilot.Contracts.Services.Spirits;
using RocoPilot.Contracts.Services.TextRecognition;
using RocoPilot.Helpers;
using RocoPilot.Models.Capture;
using RocoPilot.Models.ImageMatching;
using RocoPilot.Models.Recognition;
using RocoPilot.Services.RuntimeTasks;
using RocoPilot.Services.Recognition;

namespace RocoPilot.Services.IndependentTasks;

internal enum ManualChallengeScene { Unknown, World, Manual, ChallengePage, FlowerList, Map, Interaction, Confirmation, Preparation, Battle, MedalConfirmation, Capture, CaptureBallMissing, Result, LegendaryList, LegendaryConfirmation, UnderfilledTeam, InsufficientContractMark, CaptureConfirmation, LegendaryRewards, SkippableAnimation, MissingLegendaryMark, RewardSettlement }
internal sealed record LegendaryCaptureMarks(bool LegendarySelected,
    ImageMatchResult ContractButton, ImageMatchResult LegendaryButton);
internal sealed record ManualChallengeRow(string Name, ImageMatchResult Button, string RawName = "")
{
    public int Number { get; set; }
    public bool HasMatched { get; set; }
}
internal sealed record ManualChallengeScreen(ManualChallengeScene Scene, IReadOnlyList<ManualChallengeRow> Rows,
    ImageMatchResult? Button = null, string Text = "", BattleScreen? BattleScreen = null, ImageMatchResult? ExitButton = null,
    LegendaryCaptureMarks? Marks = null, bool HasChallengeOption = false, int Difficulty = 0);

/// <summary>界面控件用模板定位，动态名称和交互文字使用默认 OCR 确认。</summary>
public sealed class ManualChallengeScreenRecognizer(IImageMatchingService images, ITextRecognitionService ocr,
    ISpiritCatalogService catalog, IEncounterSeasonConfigService seasons,
    ILogger<ManualChallengeScreenRecognizer>? logger = null)
{
    // 首次定位与点击后的复查共用同一模板、区域和阈值。
    private static readonly Dictionary<string, (double Left, double Top, double Right, double Bottom, RecognitionAnchor Anchor)> ButtonAreas = new()
    {
        ["manual-challenge"] = (.04, .18, .13, .35, RecognitionAnchor.TopLeft),
        ["flower-tab"] = (.2, .015, .48, .1, RecognitionAnchor.TopLeft),
        ["flower-tab-inactive"] = (.2, .015, .48, .1, RecognitionAnchor.TopLeft),
        ["list-teleport"] = (.78, .12, .94, .98, RecognitionAnchor.CenterRight),
        ["map-teleport"] = (.68, .88, .92, .98, RecognitionAnchor.BottomRight),
        ["confirmation-challenge"] = (.5, .74, .72, .94, RecognitionAnchor.Center),
        ["start"] = (.49, .75, .69, .9, RecognitionAnchor.Center),
        ["result-retry"] = (.49, .88, .65, .98, RecognitionAnchor.BottomCenter),
        ["result-exit"] = (.35, .88, .49, .98, RecognitionAnchor.BottomCenter),
        ["legendary-tab"] = (.37, .015, .46, .1, RecognitionAnchor.TopLeft),
        ["legendary-difficulty-1"] = (.065, .14, .102, .195, RecognitionAnchor.TopLeft),
        ["legendary-difficulty-2"] = (.065, .235, .102, .29, RecognitionAnchor.TopLeft),
        ["legendary-difficulty-3"] = (.065, .33, .102, .385, RecognitionAnchor.TopLeft),
        ["legendary-challenge"] = (.66, .88, .94, .98, RecognitionAnchor.BottomRight),
        ["legendary-underfilled-team"] = (620d / 2048, 535d / 1152, 1450d / 2048, 700d / 1152, RecognitionAnchor.Center),
        ["legendary-underfilled-yes"] = (1060d / 2048, 885d / 1152, 1315d / 2048, 1015d / 1152, RecognitionAnchor.Center),
        ["legendary-confirm"] = (.5, .75, .65, .9, RecognitionAnchor.Center),
        ["legendary-cancel"] = (.35, .75, .49, .9, RecognitionAnchor.Center),
        ["legendary-result-retry"] = (.49, .88, .65, .98, RecognitionAnchor.BottomCenter),
        ["legendary-result-exit"] = (.35, .88, .49, .98, RecognitionAnchor.BottomCenter),
        // 获得奖励弹窗标题（礼盒图标+文字）位于弹窗左侧中部，捕捉结算专属。
        ["rewards"] = (.09, .385, .23, .455, RecognitionAnchor.Auto)
    };

    internal async Task<ManualChallengeScreen> ReadAsync(CapturedFrame frame, int clientX, int clientY,
        int width, int height, CancellationToken cancellationToken, Action<RecognitionRegion, string>? reportOcr = null,
        bool legendary = false)
    {
        var scale = Math.Min(width / 2048d, height / 1152d);
        var options = new ImageMatchOptions
        {
            MinimumScore = .94,
            TemplateScaleX = scale, TemplateScaleY = scale
        };
        // 遮罩只保留跳过图标与 Esc 键帽的内部，排除动画背景及混色边缘。
        var skip = await Match("animation-skip", .04, .015, .11, .12);
        if (skip is not null) return new(ManualChallengeScene.SkippableAnimation, [], skip);
        var tab = legendary ? await ReadLegendaryTabAsync(frame, clientX, clientY, width, height, cancellationToken)
            : await Button("flower-tab") ?? await Button("flower-tab-inactive");
        if (tab is not null)
        {
            if (!IsTabSelected(frame, tab)) return new(ManualChallengeScene.ChallengePage, [], tab);
            var (left, top, right, bottom, anchor) = ButtonAreas["list-teleport"];
            var buttons = await images.FindMatchesAsync(frame, Area(left, top, right, bottom, anchor: anchor),
                "2048x1152/flower-seed/list-teleport.png", 8, options, cancellationToken: cancellationToken);
            List<ManualChallengeRow> rows = [];
            foreach (var button in buttons.Matches.Where(button => button.IsMatch).OrderBy(button => button.Y))
            {
                var region = Area(.305, 0, .45, .0434, $"{(legendary ? "传说精灵" : "花种")}名称 {rows.Count + 1}");
                region.Y = (int)Math.Round(button.Y + button.Height / 2d - 72 * scale, MidpointRounding.AwayFromZero);
                if (region.Y < clientY + height * .14 || region.Y + region.Height > clientY + height * .97) continue;
                var name = await ReadName(region);
                rows.Add(new(name.Name, button, name.RawName));
            }
            return rows.Count == 0 ? new(ManualChallengeScene.Unknown, [])
                : new(legendary ? ManualChallengeScene.LegendaryList : ManualChallengeScene.FlowerList, rows);
        }
        var manual = await Match("manual-header", .08, .005, .2, .06, .90);
        if (manual is not null)
            return new(ManualChallengeScene.Manual, [], await Button("manual-challenge"));
        if (legendary)
        {
            // 弹窗先于后方详情页判断，避免把被遮罩的开始挑战按钮当成当前目标。
            if (await Button("legendary-underfilled-team") is not null)
                return new(ManualChallengeScene.UnderfilledTeam, [], await Button("legendary-underfilled-yes"));
            // 只匹配“无法进行捕捉”，不依赖各传说精灵的专属印记名称。
            if (await Match("legendary-missing-mark", .25, .42, .75, .66) is { } missingMark)
            {
                var cancel = await Button("legendary-cancel");
                return cancel is null ? new(ManualChallengeScene.Unknown, [])
                    : new(ManualChallengeScene.MissingLegendaryMark, [], missingMark, ExitButton: cancel);
            }
            // 只确认共有的“参与挑战将只能”，兼容印记不足与周次数用尽时不同的换行位置。
            // 文字像素模板随游戏版本的渲染变化失效（2026-11-08 实测 0.84~0.87 且跨帧浮动），
            // 改为：取消按钮出现时 OCR 正文第一行并模糊匹配。按钮模板跨帧稳定 0.95+。
            if (await Button("legendary-cancel") is not null)
            {
                // 区域按原生 1920×1080 投影实测卡在正文第一行（2048 基准 y 596–620）：
                // 上界留出弹窗底色余量，下界必须避开第二行“继续？”（626 起），
                // 单行 OCR 混入第二行残片会识别失败。
                var promptArea = Area(.31, .508, .70, .541, "契约印记不足弹窗正文");
                var insufficientPrompt = await ocr.RecognizeAsync(frame, promptArea, TextRecognitionDefaults.Method, cancellationToken);
                reportOcr?.Invoke(promptArea, string.IsNullOrWhiteSpace(insufficientPrompt.Text) ? "OCR：无文本" : $"OCR：{insufficientPrompt.Text}");
                logger?.LogDebug("契约印记不足弹窗正文：OCR={Raw}", insufficientPrompt.Text);
                if (TextMatchingHelper.CalculateSimilarity(insufficientPrompt.Text, "参与挑战将只能") >= .8)
                    return new(ManualChallengeScene.InsufficientContractMark, [], await Button("legendary-confirm"),
                        ExitButton: await Button("legendary-cancel"));
            }
            var captureConfirmation = await ReadCaptureConfirmationAsync(frame, clientX, clientY, width, height, cancellationToken);
            if (captureConfirmation is not null) return captureConfirmation;
            var rewards = await Match("legendary-rewards", .43, .34, .57, .44);
            if (rewards is not null) return new(ManualChallengeScene.LegendaryRewards, [], rewards);
            if (await Match("legendary-confirmation-header", .65, .10, .83, .22) is not null)
            {
                List<ManualChallengeRow> difficulties = [];
                var selectedDifficulty = 0;
                for (var number = 1; number <= 3; number++)
                {
                    var name = $"legendary-difficulty-{number}";
                    var (left, top, right, bottom, anchor) = ButtonAreas[name];
                    // 只匹配罗马数字及其中心底色，不使用会变形的气泡轮廓。
                    var selected = await images.MatchAsync(frame, Area(left, top, right, bottom, anchor: anchor),
                        TemplatePath(name + "-selected"), options, cancellationToken);
                    if (selected.IsMatch) selectedDifficulty = number;
                    else if (await Button(name) is { } button)
                        difficulties.Add(new(new string('I', number), button) { Number = number });
                }
                return new(ManualChallengeScene.LegendaryConfirmation, difficulties, await Button("legendary-challenge"),
                    Difficulty: selectedDifficulty);
            }
        }
        // 确认页两类花种都使用共有的“花种”标题字样。
        var confirmation = legendary ? null : await Match("confirmation-header", .5, .25, .78, .5);
        if (confirmation is not null)
        {
            var challenge = await Button("confirmation-challenge");
            var cancel = await Match("confirmation-cancel", .3, .74, .52, .94);
            return challenge is not null && cancel is not null
                ? new(ManualChallengeScene.Confirmation, [], challenge) : new(ManualChallengeScene.Unknown, []);
        }
        // 标题只匹配共有的“花种”，兼容“命定花种”和“稀兽花种”。
        var map = await Match(legendary ? "legendary-map-header" : "map-header", .65, .015, .85, .09);
        if (map is not null)
        {
            var teleport = await Button("map-teleport");
            var name = await ReadName(Area(.72, .184, .87, .2274, legendary ? "地图传说精灵名称" : "地图花种名称"));
            return new(ManualChallengeScene.Map, [], teleport, name.Name);
        }
        var preparation = await Match("preparation", .3, .3, .65, .45);
        if (preparation is not null)
            return new(ManualChallengeScene.Preparation, [], await Button("start"));
        var medal = await Match("medal-confirmation", .32, .48, .68, .61);
        if (medal is not null)
            return new(ManualChallengeScene.MedalConfirmation, [], await Button("start"));
        var result = await ReadResultAsync(frame, clientX, clientY, width, height, cancellationToken, legendary);
        if (result is not null) return result;
        var exclusive = await Match("capture-exclusive", .08, .2, .23, .4);
        var captureButton = await Match("capture-button", .88, .84, 1, 1);
        if (exclusive is not null && captureButton is not null)
            return new(ManualChallengeScene.Capture, [], captureButton);
        // 捕捉按钮在但专属球标记不在：球列表停留在普通球，需要滚轮切出专属球。
        if (exclusive is null && captureButton is not null)
            return new(ManualChallengeScene.CaptureBallMissing, [], captureButton);
        // 交互菜单必须同时具有大世界 HUD，结束动画中的浅色图形不能判为挑战失败。
        var world = await MatchWorldHudAsync(frame, clientX, clientY, width, height, cancellationToken);
        var prompt = world.IsMatch ? await Match("interaction-f", .42, .24, .84, .9) : null;
        if (prompt is not null)
        {
            var line = new RecognitionRegion
            {
                Id = "花种交互选项",
                X = (int)Math.Round(prompt.X + 90 * scale), Y = (int)Math.Round(prompt.Y - 5 * scale),
                Width = (int)Math.Round(150 * scale), Height = (int)Math.Round(40 * scale)
            };
            if (line.X + line.Width < clientX + width && line.Y >= clientY && line.Y + line.Height < clientY + height
                && IsSelectedLine(frame, line))
            {
                var text = await ocr.RecognizeAsync(frame, line, TextRecognitionDefaults.Method, cancellationToken);
                reportOcr?.Invoke(line, string.IsNullOrWhiteSpace(text.Text) ? "OCR：无文本" : $"OCR：{text.Text}");
                var selected = Normalize(text.Text);
                var hasChallenge = selected == "挑战";
                if (!hasChallenge && selected.Length > 0)
                {
                    // 只在交互图标列查找挑战，再识别对应行；不能因出现留言就原地滚轮。
                    var menu = Area(.42, .24, .84, .9);
                    menu.X = (int)Math.Round(prompt.X + 35 * scale);
                    menu.Width = (int)Math.Round(60 * scale);
                    var challenges = await images.FindMatchesAsync(frame, menu, TemplatePath("interaction-challenge"),
                        6, options, cancellationToken: cancellationToken);
                    foreach (var icon in challenges.Matches.Where(match => match.IsMatch))
                    {
                        var option = new RecognitionRegion
                        {
                            Id = "挑战交互选项",
                            X = (int)Math.Round(icon.X + 42 * scale), Y = (int)Math.Round(icon.Y - 2 * scale),
                            Width = line.Width, Height = line.Height
                        };
                        var candidate = await ocr.RecognizeAsync(frame, option, TextRecognitionDefaults.Method, cancellationToken);
                        reportOcr?.Invoke(option, $"OCR：{candidate.Text}");
                        if (Normalize(candidate.Text) != "挑战") continue;
                        hasChallenge = true;
                        break;
                    }
                }
                return new(ManualChallengeScene.Interaction, [], world, Text: selected, HasChallengeOption: hasChallenge);
            }
        }
        // 只采用游戏 HUD 与战斗按钮的正向匹配，未知画面不会由上一输入推断。
        if (world.IsMatch) return new(ManualChallengeScene.World, [], world);
        // 传说挑战倒下后仍显示“技能”按钮；用左侧候选精灵血条外框和战斗聊天共同确认。
        // 血条模板遮掉填充部分，不依赖候选精灵的剩余生命值。
        if (legendary && (await Match("legendary-pet-health", .10, .25, .25, .80)
                ?? await Match("legendary-pet-health-selected", .10, .25, .25, .80)) is { } petHealth
            && (await images.MatchAsync(frame, Area(.92, .62, 1, .75), "2048x1152/battle-chat.png",
                options, cancellationToken)).IsMatch)
        {
            var confirm = await Match("legendary-pet-confirmation", .45, .6, .72, .88);
            return new(ManualChallengeScene.Battle, [], confirm ?? petHealth,
                BattleScreen: confirm is null ? BattleScreen.PetSwitching : BattleScreen.PetSwitchConfirmation);
        }
        foreach (var (template, screen) in new[]
        {
            ("battle-button-skill", BattleScreen.SkillSelection),
            ("battle-button-change", BattleScreen.PetSwitching),
            ("battle-chat", BattleScreen.Chat)
        })
        {
            var region = screen == BattleScreen.Chat ? Area(.92, .62, 1, .75) : Area(.68, .62, 1, 1);
            var battle = await images.MatchAsync(frame, region, "2048x1152/" + template + ".png", options, cancellationToken);
            if (battle.IsMatch) return new(ManualChallengeScene.Battle, [], battle, BattleScreen: screen);
        }
        // 奖励弹窗放在正向匹配之后兜底，避免雪地等低纹理画面误判。
        if (!legendary && await Button("rewards") is not null)
            return new(ManualChallengeScene.RewardSettlement, [], RewardsClickPoint(clientX, clientY, width, height));
        return new(ManualChallengeScene.Unknown, []);

        Task<ImageMatchResult?> Button(string template)
            => MatchButtonAsync(frame, template, clientX, clientY, width, height, cancellationToken);

        RecognitionRegion Area(double left, double top, double right, double bottom, string id = "", RecognitionAnchor anchor = RecognitionAnchor.Auto)
        {
            var region = CreateArea(clientX, clientY, width, height, left, top, right, bottom, anchor);
            region.Id = id;
            return region;
        }
        async Task<ImageMatchResult?> Match(string template, double left, double top, double right, double bottom, double score = .94)
        {
            options.MinimumScore = score;
            var result = await images.MatchAsync(frame, Area(left, top, right, bottom), TemplatePath(template), options, cancellationToken);
            options.MinimumScore = .94;
            return result.IsMatch ? result : null;
        }
        async Task<(string Name, string RawName)> ReadName(RecognitionRegion region)
        {
            var result = await ocr.RecognizeAsync(frame, region, TextRecognitionDefaults.Method, cancellationToken);
            var name = string.IsNullOrWhiteSpace(result.Text) ? string.Empty
                : await catalog.MatchSpiritNameAsync(result.Text, seasons.Load().SpiritNameMatchThreshold, cancellationToken);
            reportOcr?.Invoke(region, $"OCR：{(string.IsNullOrWhiteSpace(result.Text) ? "无文本" : result.Text)} → 图鉴：{(string.IsNullOrEmpty(name) ? "未匹配" : name)}");
            return (legendary && string.IsNullOrEmpty(name) ? Normalize(result.Text) : name, result.Text);
        }
    }

    // 复查原点击目标，不要求下一页出现。列表有多个相同按钮，只复查原目标行。
    internal async Task<ImageMatchResult?> FindClickTargetAsync(CapturedFrame frame, ManualChallengeScene scene,
        string templatePath, double rowY, int clientX, int clientY, int width, int height, CancellationToken token,
        bool legendary = false)
    {
        if (scene is ManualChallengeScene.World or ManualChallengeScene.Interaction)
        {
            var hud = await MatchWorldHudAsync(frame, clientX, clientY, width, height, token);
            return hud.IsMatch ? hud : null;
        }
        if (scene == ManualChallengeScene.CaptureConfirmation)
        {
            var current = await ReadCaptureConfirmationAsync(frame, clientX, clientY, width, height, token);
            var selecting = templatePath is "legendary-select-contract" or "legendary-select-mark";
            if (current is null && !selecting) return null;
            // 未识别到选中状态时继续等待，不能将识别失败当成切换成功。
            if (current?.Marks is not { } marks) return ImageMatchResult.NoMatch(0, templatePath);
            if (templatePath == "legendary-select-contract") return marks.LegendarySelected ? marks.ContractButton : null;
            if (templatePath == "legendary-select-mark") return marks.LegendarySelected ? null : marks.LegendaryButton;
            return current.Button;
        }
        if (scene == ManualChallengeScene.ChallengePage)
        {
            var tab = legendary ? await ReadLegendaryTabAsync(frame, clientX, clientY, width, height, token)
                : await MatchButtonAsync(frame, "flower-tab", clientX, clientY, width, height, token)
                ?? await MatchButtonAsync(frame, "flower-tab-inactive", clientX, clientY, width, height, token);
            return tab is not null && !IsTabSelected(frame, tab) ? tab : null;
        }
        if (scene == ManualChallengeScene.RewardSettlement)
        {
            return await FindRewardsHeaderAsync(frame, clientX, clientY, width, height, token) is not null
                ? RewardsClickPoint(clientX, clientY, width, height) : null;
        }
        var template = Path.GetFileNameWithoutExtension(templatePath);
        if (templatePath.Replace('\\', '/').Contains("/legendary/")) template = "legendary-" + template;
        if (scene == ManualChallengeScene.UnderfilledTeam)
        {
            var body = await MatchButtonAsync(frame, "legendary-underfilled-team", clientX, clientY, width, height, token);
            var yes = await MatchButtonAsync(frame, "legendary-underfilled-yes", clientX, clientY, width, height, token);
            // 只命中其中一项时继续复查，等两个特征一致后再点击或交回主流程。
            if (body is null && yes is null) return null;
            return body is not null && yes is not null ? yes : ImageMatchResult.NoMatch(0, templatePath);
        }
        if (template is "legendary-challenge" or "legendary-result-retry"
            && await MatchButtonAsync(frame, "legendary-underfilled-team", clientX, clientY, width, height, token) is not null)
            return null;
        // 契约印记不足弹窗弹出后原按钮被遮罩压暗、模板自然失配，复查无需额外让路；
        // 曾尝试以 legendary-cancel 出现为标志让路，会在准备页误命中，已回退。
        return await MatchButtonAsync(frame, template,
            clientX, clientY, width, height, token,
            scene is ManualChallengeScene.FlowerList or ManualChallengeScene.LegendaryList ? rowY : null);
    }

    /// <summary>识别获得奖励弹窗标题。</summary>
    internal Task<ImageMatchResult?> FindRewardsHeaderAsync(CapturedFrame frame, int clientX, int clientY,
        int width, int height, CancellationToken token)
        => MatchButtonAsync(frame, "rewards", clientX, clientY, width, height, token);

    // 弹窗无按钮，点击底部中央（客户区 50%/90%）即可关闭。
    private static ImageMatchResult RewardsClickPoint(int clientX, int clientY, int width, int height)
        => new(true, 1, clientX + (int)Math.Round(width * .5) - 1, clientY + (int)Math.Round(height * .9) - 1,
            2, 2, TemplatePath("rewards"));
    

    private async Task<ImageMatchResult?> ReadLegendaryTabAsync(CapturedFrame frame, int clientX, int clientY,
        int width, int height, CancellationToken token)
    {
        var selected = await MatchButtonAsync(frame, "legendary-tab", clientX, clientY, width, height, token);
        if (selected is not null) return selected;
        // 其它挑战标签选中时，传说文字变灰；在花种标签确认的同一栏读取文字。
        if (await MatchButtonAsync(frame, "flower-tab", clientX, clientY, width, height, token) is null
            && await MatchButtonAsync(frame, "flower-tab-inactive", clientX, clientY, width, height, token) is null)
            return null;
        var region = CreateArea(clientX, clientY, width, height, 780d / 2048, 42d / 1152, 918d / 2048, 82d / 1152,
            RecognitionAnchor.TopLeft);
        var text = await ocr.RecognizeAsync(frame, region, TextRecognitionDefaults.Method, token);
        if (Normalize(text.Text) != "传说精灵") return null;
        var button = CreateArea(clientX, clientY, width, height, 790d / 2048, 48d / 1152, 902d / 2048, 76d / 1152,
            RecognitionAnchor.TopLeft);
        return new(true, 1, button.X, button.Y, button.Width, button.Height, TemplatePath("legendary-tab"));
    }

    private static string TemplatePath(string name) => name.StartsWith("legendary-", StringComparison.Ordinal)
        ? $"2048x1152/legendary/{name[10..]}.png" : $"2048x1152/flower-seed/{name}.png";

    /// <summary>
    /// 读取捕捉界面右上角的凝练印记（紫色星星）状态：
    /// 是否已选中（白色高亮胶囊）与是否弹出"凝练印记不足，无法捕捉"提示
    /// （数量耗尽的直接标志）。不做数量 OCR——小数字识别不可靠，
    /// 不足时由游戏提示兜底切换蓝星。
    /// </summary>
    internal async Task<(bool Selected, bool Insufficient)> ReadCondensedMarkAsync(
        CapturedFrame frame, int clientX, int clientY, int width, int height, CancellationToken token)
    {
        var scale = Math.Min(width / 2048d, height / 1152d);
        var options = new ImageMatchOptions
        {
            // 选中/未选中实测约 0.98 / 0.13，窗口等比缩放误差会压低选中得分，取 0.85 留余量。
            MinimumScore = .85,
            TemplateScaleX = scale,
            TemplateScaleY = scale
        };
        // 坐标基于原生窗口截图校准（用户截图含 17.4px 标题栏已扣除）：
        // 紫星按钮窗口位 (1795-1884, 111-159) → 2048 基准 (1914-2010, 119-170)。
        var selectedArea = CreateArea(clientX, clientY, width, height, .925, .098, .99, .155);
        selectedArea.Id = "凝练印记选择";
        var selected = await images.MatchAsync(frame, selectedArea, TemplatePath("condensed-selected"), options, token);
        // 不足提示：屏幕上方居中的"凝练印记不足，无法捕捉"横条（实测 0.99 / 0.10 区分）。
        var promptArea = CreateArea(clientX, clientY, width, height, .33, .125, .68, .18);
        promptArea.Id = "凝练印记不足提示";
        var prompt = await images.MatchAsync(frame, promptArea, TemplatePath("condensed-insufficient"), options, token);
        logger?.LogDebug("凝练印记状态：选中={Selected}，不足提示={Insufficient}，得分={Score:F3}",
            selected.IsMatch, prompt.IsMatch, prompt.Score);
        return (selected.IsMatch, prompt.IsMatch);
    }

    private async Task<ManualChallengeScreen?> ReadCaptureConfirmationAsync(CapturedFrame frame, int x, int y,
        int width, int height, CancellationToken token)
    {
        var scale = Math.Min(width / 2048d, height / 1152d);
        var options = new ImageMatchOptions { MinimumScore = .94, TemplateScaleX = scale, TemplateScaleY = scale };
        var header = await images.MatchAsync(frame, CreateArea(x, y, width, height, .44, .34, .56, .41),
            TemplatePath("legendary-capture-header"), options, token);
        if (!header.IsMatch) return null;
        // 无专属印记时只显示居中的契约卡片；同一区域覆盖左侧与居中位置。
        var contractArea = CreateArea(x, y, width, height, .42, .45, .55, .56);
        var markArea = CreateArea(x, y, width, height, .50, .45, .58, .56);
        var contract = await images.MatchAsync(frame, contractArea, TemplatePath("legendary-contract-selected"), options, token);
        var unselected = await images.MatchAsync(frame, contractArea, TemplatePath("legendary-contract-unselected"), options, token);
        // 只识别契约卡片的两种外观，避免依赖各赛季专属印记图案。
        var known = contract.IsMatch != unselected.IsMatch;
        return new(ManualChallengeScene.CaptureConfirmation, [],
            await MatchButtonAsync(frame, "legendary-confirm", x, y, width, height, token),
            Marks: known ? new(unselected.IsMatch,
                (contract.IsMatch ? contract : unselected) with { TemplatePath = "legendary-select-contract" },
                new(true, 1, markArea.X, markArea.Y, markArea.Width, markArea.Height, "legendary-select-mark")) : null);
    }

    // F3 的完成确认只复查原 HUD，避免全界面识别耗时影响开关按键的等待。
    private Task<ImageMatchResult> MatchWorldHudAsync(CapturedFrame frame, int clientX, int clientY,
        int width, int height, CancellationToken token)
    {
        var scale = Math.Min(width / 2048d, height / 1152d);
        return images.MatchAsync(frame, CreateArea(clientX, clientY, width, height, .08, .02, .2, .09),
            "2048x1152/magic-point.png",
            new ImageMatchOptions { MinimumScore = .94, TemplateScaleX = scale, TemplateScaleY = scale }, token);
    }

    private async Task<ImageMatchResult?> MatchButtonAsync(CapturedFrame frame, string template,
        int clientX, int clientY, int width, int height, CancellationToken token, double? rowY = null)
    {
        var (left, top, right, bottom, anchor) = ButtonAreas[template];
        var area = CreateArea(clientX, clientY, width, height, left, top, right, bottom, anchor);
        if (rowY is { } center)
        {
            // 行间距大于按钮高度；限制纵向搜索范围，避免误点邻近花种。
            var y = Math.Max(area.Y, clientY + (int)Math.Round((center - .02) * height));
            var end = Math.Min(area.Y + area.Height, clientY + (int)Math.Round((center + .02) * height));
            area.Y = y;
            area.Height = Math.Max(0, end - y);
        }
        var scale = Math.Min(width / 2048d, height / 1152d);
        var result = await images.MatchAsync(frame, area,
            TemplatePath(template),
            new ImageMatchOptions { MinimumScore = .94, TemplateScaleX = scale, TemplateScaleY = scale }, token);
        return result.IsMatch ? result : null;
    }

    internal async Task<ManualChallengeScreen?> ReadResultAsync(CapturedFrame frame, int clientX, int clientY,
        int width, int height, CancellationToken token, bool legendary = false)
    {
        var retry = await MatchButtonAsync(frame, legendary ? "legendary-result-retry" : "result-retry",
            clientX, clientY, width, height, token);
        if (retry is null) return null;
        var exit = await MatchButtonAsync(frame, legendary ? "legendary-result-exit" : "result-exit",
            clientX, clientY, width, height, token);
        return exit is null ? null : new(ManualChallengeScene.Result, [], retry, ExitButton: exit);
    }

    private static RecognitionRegion CreateArea(int x, int y, int width, int height,
        double left, double top, double right, double bottom, RecognitionAnchor anchor = RecognitionAnchor.Auto) =>
        RecognitionRegionImageHelper.MapReferenceArea(x, y, width, height, 2048, 1152,
            left * 2048, top * 1152, (right - left) * 2048, (bottom - top) * 1152, anchor);

    internal static string Normalize(string text) => string.Concat(text.Where(char.IsLetterOrDigit));
    private static bool IsTabSelected(CapturedFrame frame, ImageMatchResult tab)
    {
        var count = 0;
        for (var column = -2; column <= 2; column++)
        {
            var x = (int)Math.Round(tab.X + tab.Width / 2d + tab.Width * column * .12);
            var y = (int)Math.Round(tab.Y - tab.Height * .18);
            if (x < 0 || y < 0 || x >= frame.Width || y >= frame.Height) return false;
            var offset = (y * frame.Width + x) * 4;
            if (frame.Pixels[offset + 2] >= 234 && frame.Pixels[offset + 1] >= 228 && frame.Pixels[offset] >= 210) count++;
        }
        return count >= 4;
    }
    private static bool IsSelectedLine(CapturedFrame frame, RecognitionRegion line)
    {
        var light = 0;
        for (var row = -2; row <= 2; row++)
        for (var column = 0; column < 5; column++)
        {
            var x = (int)Math.Round(line.X + line.Width + line.Height * (.65 + column * .14));
            var y = (int)Math.Round(line.Y + line.Height / 2d + line.Height * row * .12);
            if (x < 0 || y < 0 || x >= frame.Width || y >= frame.Height) return false;
            var offset = (y * frame.Width + x) * 4;
            var b = frame.Pixels[offset]; var g = frame.Pixels[offset + 1]; var r = frame.Pixels[offset + 2];
            if (Math.Min(r, Math.Min(g, b)) >= 175 && Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) < 75) light++;
        }
        return light >= 20;
    }
}

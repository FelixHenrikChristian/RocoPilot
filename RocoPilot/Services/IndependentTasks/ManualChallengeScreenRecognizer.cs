using Microsoft.Extensions.Logging;
using RocoPilot.Configuration;
using RocoPilot.Contracts.Services.Encounters;
using RocoPilot.Contracts.Services.ImageMatching;
using RocoPilot.Contracts.Services.Spirits;
using RocoPilot.Contracts.Services.TextRecognition;
using RocoPilot.Models.Capture;
using RocoPilot.Models.ImageMatching;
using RocoPilot.Models.Recognition;
using RocoPilot.Services.RuntimeTasks;
using RocoPilot.Services.Recognition;

namespace RocoPilot.Services.IndependentTasks;

internal enum ManualChallengeScene { Unknown, World, Manual, ChallengePage, FlowerList, Map, Interaction, Confirmation, Preparation, Battle, MedalConfirmation, Capture, Rewards, Result, LegendaryList, LegendaryConfirmation, InsufficientContractMark, CaptureConfirmation, LegendaryRewards, SkippableAnimation }
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

/// <summary>界面控件用模板定位，奖励标题和花种名称使用默认 OCR 确认。</summary>
public sealed class ManualChallengeScreenRecognizer(IImageMatchingService images, ITextRecognitionService ocr,
    ISpiritCatalogService catalog, IEncounterSeasonConfigService seasons, ILogger<ManualChallengeScreenRecognizer> logger)
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
        ["legendary-confirm"] = (.5, .75, .65, .9, RecognitionAnchor.Center),
        ["legendary-cancel"] = (.35, .75, .49, .9, RecognitionAnchor.Center),
        ["legendary-result-retry"] = (.49, .88, .65, .98, RecognitionAnchor.BottomCenter),
        ["legendary-result-exit"] = (.35, .88, .49, .98, RecognitionAnchor.BottomCenter)
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
                var region = Area(.305, 0, .45, .0434, $"花种名称 {rows.Count + 1}");
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
            if (await Match("legendary-insufficient", .32, .48, .69, .61) is not null)
                return new(ManualChallengeScene.InsufficientContractMark, [], await Button("legendary-confirm"),
                    ExitButton: await Button("legendary-cancel"));
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
            var name = await ReadName(Area(.72, .184, .87, .2274, "地图花种名称"));
            return new(ManualChallengeScene.Map, [], teleport, name.Name);
        }
        var preparation = await Match("preparation", .3, .3, .65, .45);
        if (preparation is not null)
            return new(ManualChallengeScene.Preparation, [], await Button("start"));
        var medal = await Match("medal-confirmation", .32, .48, .68, .61);
        if (medal is not null)
            return new(ManualChallengeScene.MedalConfirmation, [], await Button("start"));
        var retry = await Button(legendary ? "legendary-result-retry" : "result-retry");
        if (retry is not null)
        {
            var exit = await Button(legendary ? "legendary-result-exit" : "result-exit");
            if (exit is not null) return new(ManualChallengeScene.Result, [], retry, ExitButton: exit);
        }
        var exclusive = await Match("capture-exclusive", .08, .2, .23, .4);
        if (exclusive is not null)
        {
            var capture = await Match("capture-button", .88, .84, 1, 1);
            if (capture is not null) return new(ManualChallengeScene.Capture, [], capture);
        }
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
        return await ReadRewardsAsync(frame, clientX, clientY, width, height, cancellationToken, reportOcr)
            ?? new(ManualChallengeScene.Unknown, []);

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
            if (template == "manual-header")
                logger.LogDebug("花种手册识别：匹配={Matched}，得分={Score:F3}，预计比例={Scale:F4}，目标=({X},{Y},{Width},{Height})",
                    result.IsMatch, result.Score, scale, result.X, result.Y, result.Width, result.Height);
            options.MinimumScore = .94;
            return result.IsMatch ? result : null;
        }
        async Task<(string Name, string RawName)> ReadName(RecognitionRegion region)
        {
            var result = await ocr.RecognizeAsync(frame, region, TextRecognitionDefaults.Method, cancellationToken);
            var name = legendary ? Models.Runtime.IndependentTaskSettings.LegendaryTargets.FirstOrDefault(
                    candidate => candidate == Normalize(result.Text)) ?? string.Empty
                : string.IsNullOrWhiteSpace(result.Text) ? string.Empty
                : await catalog.MatchSpiritNameAsync(result.Text, seasons.Load().SpiritNameMatchThreshold, cancellationToken);
            logger.LogDebug("花种名称识别：OCR={RawName}，图鉴={MatchedName}，区域=({X},{Y},{Width},{Height})",
                result.Text, name, region.X, region.Y, region.Width, region.Height);
            reportOcr?.Invoke(region, $"OCR：{(string.IsNullOrWhiteSpace(result.Text) ? "无文本" : result.Text)} → 图鉴：{(string.IsNullOrEmpty(name) ? "未匹配" : name)}");
            return (name, result.Text);
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
        if (scene == ManualChallengeScene.Rewards)
            return (await ReadRewardsAsync(frame, clientX, clientY, width, height, token))?.Button;
        if (scene == ManualChallengeScene.CaptureConfirmation)
        {
            var current = await ReadCaptureConfirmationAsync(frame, clientX, clientY, width, height, token);
            if (current?.Marks is not { } marks) return null;
            if (templatePath == "legendary-select-contract") return marks.LegendarySelected ? marks.ContractButton : null;
            if (templatePath == "legendary-select-mark") return marks.LegendarySelected ? null : marks.LegendaryButton;
        }
        if (scene == ManualChallengeScene.ChallengePage)
        {
            var tab = legendary ? await ReadLegendaryTabAsync(frame, clientX, clientY, width, height, token)
                : await MatchButtonAsync(frame, "flower-tab", clientX, clientY, width, height, token)
                ?? await MatchButtonAsync(frame, "flower-tab-inactive", clientX, clientY, width, height, token);
            return tab is not null && !IsTabSelected(frame, tab) ? tab : null;
        }
        var template = Path.GetFileNameWithoutExtension(templatePath);
        if (templatePath.Replace('\\', '/').Contains("/legendary/")) template = "legendary-" + template;
        return await MatchButtonAsync(frame, template,
            clientX, clientY, width, height, token,
            scene is ManualChallengeScene.FlowerList or ManualChallengeScene.LegendaryList ? rowY : null);
    }

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

    private async Task<ManualChallengeScreen?> ReadCaptureConfirmationAsync(CapturedFrame frame, int x, int y,
        int width, int height, CancellationToken token)
    {
        var scale = Math.Min(width / 2048d, height / 1152d);
        var options = new ImageMatchOptions { MinimumScore = .94, TemplateScaleX = scale, TemplateScaleY = scale };
        var header = await images.MatchAsync(frame, CreateArea(x, y, width, height, .44, .34, .56, .41),
            TemplatePath("legendary-capture-header"), options, token);
        if (!header.IsMatch) return null;
        var contractArea = CreateArea(x, y, width, height, .42, .45, .50, .56);
        var markArea = CreateArea(x, y, width, height, .50, .45, .58, .56);
        var contract = await images.MatchAsync(frame, contractArea, TemplatePath("legendary-contract-selected"), options, token);
        var mark = await images.MatchAsync(frame, markArea, TemplatePath("legendary-mark-selected"), options, token);
        if (!contract.IsMatch && !mark.IsMatch) return new(ManualChallengeScene.Unknown, []);
        return new(ManualChallengeScene.CaptureConfirmation, [],
            await MatchButtonAsync(frame, "legendary-confirm", x, y, width, height, token),
            Marks: new(mark.IsMatch,
                new(true, 1, contractArea.X, contractArea.Y, contractArea.Width, contractArea.Height, "legendary-select-contract"),
                new(true, 1, markArea.X, markArea.Y, markArea.Width, markArea.Height, "legendary-select-mark")));
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
        if (template == "manual-challenge")
            logger.LogDebug("花种手册挑战入口：匹配={Matched}，得分={Score:F3}，目标=({X},{Y},{Width},{Height})",
                result.IsMatch, result.Score, result.X, result.Y, result.Width, result.Height);
        return result.IsMatch ? result : null;
    }

    private async Task<ManualChallengeScreen?> ReadRewardsAsync(CapturedFrame frame, int clientX, int clientY,
        int width, int height, CancellationToken token, Action<RecognitionRegion, string>? reportOcr = null)
    {
        // 单排和双排奖励的标题高度不同；只识别单行文字。
        foreach (var (top, bottom) in new[] { (.4, .445), (.47, .505) })
        {
            var region = CreateArea(clientX, clientY, width, height, .132, top, .199, bottom);
            region.Id = "花种奖励标题";
            var result = await ocr.RecognizeAsync(frame, region, TextRecognitionDefaults.Method, token);
            var matched = Normalize(result.Text) == "获得奖励";
            logger.LogDebug("花种奖励识别：OCR={Text}，匹配={Matched}，区域=({X},{Y},{Width},{Height})",
                result.Text, matched, region.X, region.Y, region.Width, region.Height);
            if (!matched) continue;
            reportOcr?.Invoke(region, $"OCR：{result.Text}");
            return new(ManualChallengeScene.Rewards, [], new ImageMatchResult(true, 1,
                clientX + (int)Math.Round(width * .5), clientY + (int)Math.Round(height * .9), 1, 1, "flower-rewards-dismiss"), Text: result.Text);
        }
        return null;
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

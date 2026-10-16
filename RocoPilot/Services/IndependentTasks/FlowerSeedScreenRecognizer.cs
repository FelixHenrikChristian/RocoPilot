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

namespace RocoPilot.Services.IndependentTasks;

internal enum FlowerSeedScene { Unknown, World, Manual, ChallengePage, FlowerList, Map, Interaction, Confirmation, Preparation, Battle, MedalConfirmation, Capture, Rewards, Result }
internal sealed record FlowerSeedRow(string Name, ImageMatchResult Button, string RawName = "")
{
    public int Number { get; set; }
    public bool HasMatched { get; set; }
}
internal sealed record FlowerSeedScreen(FlowerSeedScene Scene, IReadOnlyList<FlowerSeedRow> Rows,
    ImageMatchResult? Button = null, string Text = "", BattleScreen? BattleScreen = null, ImageMatchResult? ExitButton = null);

/// <summary>界面控件用模板定位，奖励标题和花种名称使用默认 OCR 确认。</summary>
public sealed class FlowerSeedScreenRecognizer(IImageMatchingService images, ITextRecognitionService ocr,
    ISpiritCatalogService catalog, IEncounterSeasonConfigService seasons, ILogger<FlowerSeedScreenRecognizer> logger)
{
    // 首次定位与点击后的复查共用同一模板、区域和阈值。
    private static readonly Dictionary<string, (double Left, double Top, double Right, double Bottom)> ButtonAreas = new()
    {
        ["flower-tab"] = (.2, .015, .48, .1),
        ["flower-tab-inactive"] = (.2, .015, .48, .1),
        ["list-teleport"] = (.78, .12, .94, .98),
        ["map-teleport"] = (.68, .88, .92, .98),
        ["confirmation-challenge"] = (.5, .74, .72, .94),
        ["start"] = (.49, .75, .69, .9),
        ["result-retry"] = (.49, .88, .65, .98),
        ["result-exit"] = (.35, .88, .49, .98)
    };

    internal async Task<FlowerSeedScreen> ReadAsync(CapturedFrame frame, int clientX, int clientY,
        int width, int height, CancellationToken cancellationToken, Action<RecognitionRegion, string>? reportOcr = null)
    {
        var scale = height / 1152d;
        var options = new ImageMatchOptions
        {
            MinimumScore = .94,
            TemplateScaleX = scale, TemplateScaleY = scale
        };
        var tab = await Button("flower-tab") ?? await Button("flower-tab-inactive");
        if (tab is not null)
        {
            if (!IsTabSelected(frame, tab)) return new(FlowerSeedScene.ChallengePage, [], tab);
            var (left, top, right, bottom) = ButtonAreas["list-teleport"];
            var buttons = await images.FindMatchesAsync(frame, Area(left, top, right, bottom),
                "2048x1152/flower-seed/list-teleport.png", 8, options, cancellationToken: cancellationToken);
            List<FlowerSeedRow> rows = [];
            foreach (var button in buttons.Matches.Where(button => button.IsMatch).OrderBy(button => button.Y))
            {
                var region = Area(.305, 0, .45, .0434, $"花种名称 {rows.Count + 1}");
                region.Y = (int)Math.Round(button.Y + button.Height / 2d - 72 * scale, MidpointRounding.AwayFromZero);
                if (region.Y < clientY + height * .14 || region.Y + region.Height > clientY + height * .97) continue;
                var name = await ReadName(region);
                rows.Add(new(name.Name, button, name.RawName));
            }
            return rows.Count == 0 ? new(FlowerSeedScene.Unknown, []) : new(FlowerSeedScene.FlowerList, rows);
        }
        var manual = await Match("manual-header", .08, .005, .2, .06, .90);
        if (manual is not null)
            return new(FlowerSeedScene.Manual, [], new ImageMatchResult(true, manual.Score,
                clientX + (int)Math.Round(140 * scale), clientY + (int)Math.Round(280 * scale),
                (int)Math.Round(60 * scale), (int)Math.Round(60 * scale), "manual-challenge-navigation"));
        // 确认页两类花种都使用共有的“花种”标题字样。
        var confirmation = await Match("confirmation-header", .5, .25, .78, .5);
        if (confirmation is not null)
        {
            var challenge = await Button("confirmation-challenge");
            var cancel = await Match("confirmation-cancel", .3, .74, .52, .94);
            return challenge is not null && cancel is not null
                ? new(FlowerSeedScene.Confirmation, [], challenge) : new(FlowerSeedScene.Unknown, []);
        }
        // 标题只匹配共有的“花种”，兼容“命定花种”和“稀兽花种”。
        var map = await Match("map-header", .65, .015, .85, .09);
        if (map is not null)
        {
            var teleport = await Button("map-teleport");
            var name = await ReadName(Area(.72, .184, .87, .2274, "地图花种名称"));
            return new(FlowerSeedScene.Map, [], teleport, name.Name);
        }
        var preparation = await Match("preparation", .3, .3, .65, .45);
        if (preparation is not null)
            return new(FlowerSeedScene.Preparation, [], await Button("start"));
        var medal = await Match("medal-confirmation", .32, .48, .68, .61);
        if (medal is not null)
            return new(FlowerSeedScene.MedalConfirmation, [], await Button("start"));
        var retry = await Button("result-retry");
        if (retry is not null)
        {
            var exit = await Button("result-exit");
            if (exit is not null) return new(FlowerSeedScene.Result, [], retry, ExitButton: exit);
        }
        var exclusive = await Match("capture-exclusive", .08, .2, .23, .4);
        if (exclusive is not null)
        {
            var capture = await Match("capture-button", .88, .84, 1, 1);
            if (capture is not null) return new(FlowerSeedScene.Capture, [], capture);
        }
        // 交互菜单必须同时具有大世界 HUD，结束动画中的浅色图形不能判为挑战失败。
        var world = await images.MatchAsync(frame, Area(.08, .02, .2, .09), "2048x1152/magic-point.png", options, cancellationToken);
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
                return new(FlowerSeedScene.Interaction, [], world, Text: Normalize(text.Text));
            }
        }
        // 只采用游戏 HUD 与战斗按钮的正向匹配，未知画面不会由上一输入推断。
        if (world.IsMatch) return new(FlowerSeedScene.World, [], world);
        foreach (var (template, screen) in new[]
        {
            ("battle-button-skill", BattleScreen.SkillSelection),
            ("battle-button-change", BattleScreen.PetSwitching),
            ("battle-chat", BattleScreen.Chat)
        })
        {
            var region = screen == BattleScreen.Chat ? Area(.92, .62, 1, .75) : Area(.68, .62, 1, 1);
            var battle = await images.MatchAsync(frame, region, "2048x1152/" + template + ".png", options, cancellationToken);
            if (battle.IsMatch) return new(FlowerSeedScene.Battle, [], battle, BattleScreen: screen);
        }
        return await ReadRewardsAsync(frame, clientX, clientY, width, height, cancellationToken, reportOcr)
            ?? new(FlowerSeedScene.Unknown, []);

        Task<ImageMatchResult?> Button(string template)
            => MatchButtonAsync(frame, template, clientX, clientY, width, height, cancellationToken);

        RecognitionRegion Area(double left, double top, double right, double bottom, string id = "")
        {
            var region = CreateArea(clientX, clientY, width, height, left, top, right, bottom);
            region.Id = id;
            return region;
        }
        async Task<ImageMatchResult?> Match(string template, double left, double top, double right, double bottom, double score = .94)
        {
            options.MinimumScore = score;
            var result = await images.MatchAsync(frame, Area(left, top, right, bottom), "2048x1152/flower-seed/" + template + ".png", options, cancellationToken);
            options.MinimumScore = .94;
            return result.IsMatch ? result : null;
        }
        async Task<(string Name, string RawName)> ReadName(RecognitionRegion region)
        {
            var result = await ocr.RecognizeAsync(frame, region, TextRecognitionDefaults.Method, cancellationToken);
            var name = string.IsNullOrWhiteSpace(result.Text) ? string.Empty
                : await catalog.MatchSpiritNameAsync(result.Text, seasons.Load().SpiritNameMatchThreshold, cancellationToken);
            logger.LogDebug("花种名称识别：OCR={RawName}，图鉴={MatchedName}，区域=({X},{Y},{Width},{Height})",
                result.Text, name, region.X, region.Y, region.Width, region.Height);
            reportOcr?.Invoke(region, $"OCR：{(string.IsNullOrWhiteSpace(result.Text) ? "无文本" : result.Text)} → 图鉴：{(string.IsNullOrEmpty(name) ? "未匹配" : name)}");
            return (name, result.Text);
        }
    }

    // 复查原点击目标，不要求下一页出现。列表有多个相同按钮，只复查原目标行。
    internal async Task<ImageMatchResult?> FindClickTargetAsync(CapturedFrame frame, FlowerSeedScene scene,
        string templatePath, double rowY, int clientX, int clientY, int width, int height, CancellationToken token)
    {
        if (scene == FlowerSeedScene.Rewards)
            return (await ReadRewardsAsync(frame, clientX, clientY, width, height, token))?.Button;
        if (scene == FlowerSeedScene.Manual)
        {
            // 手册入口是相对标题的坐标，并非独立按钮模板，沿用手册自身的识别规则。
            var screen = await ReadAsync(frame, clientX, clientY, width, height, token);
            return screen.Scene == FlowerSeedScene.Manual ? screen.Button : null;
        }
        if (scene == FlowerSeedScene.ChallengePage)
        {
            var tab = await MatchButtonAsync(frame, "flower-tab", clientX, clientY, width, height, token)
                ?? await MatchButtonAsync(frame, "flower-tab-inactive", clientX, clientY, width, height, token);
            return tab is not null && !IsTabSelected(frame, tab) ? tab : null;
        }
        return await MatchButtonAsync(frame, Path.GetFileNameWithoutExtension(templatePath),
            clientX, clientY, width, height, token, scene == FlowerSeedScene.FlowerList ? rowY : null);
    }

    private async Task<ImageMatchResult?> MatchButtonAsync(CapturedFrame frame, string template,
        int clientX, int clientY, int width, int height, CancellationToken token, double? rowY = null)
    {
        var (left, top, right, bottom) = ButtonAreas[template];
        if (rowY is { } center)
        {
            // 行间距大于按钮高度；限制纵向搜索范围，避免误点邻近花种。
            top = Math.Max(top, center - .02);
            bottom = Math.Min(bottom, center + .02);
        }
        var scale = height / 1152d;
        var result = await images.MatchAsync(frame, CreateArea(clientX, clientY, width, height, left, top, right, bottom),
            $"2048x1152/flower-seed/{template}.png",
            new ImageMatchOptions { MinimumScore = .94, TemplateScaleX = scale, TemplateScaleY = scale }, token);
        return result.IsMatch ? result : null;
    }

    private async Task<FlowerSeedScreen?> ReadRewardsAsync(CapturedFrame frame, int clientX, int clientY,
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
            return new(FlowerSeedScene.Rewards, [], new ImageMatchResult(true, 1,
                clientX + (int)Math.Round(width * .5), clientY + (int)Math.Round(height * .9), 1, 1, "flower-rewards-dismiss"), Text: result.Text);
        }
        return null;
    }

    private static RecognitionRegion CreateArea(int x, int y, int width, int height,
        double left, double top, double right, double bottom) => new()
    {
        X = x + (int)Math.Round(width * left), Y = y + (int)Math.Round(height * top),
        Width = Math.Max(1, (int)Math.Round(width * (right - left))),
        Height = Math.Max(1, (int)Math.Round(height * (bottom - top)))
    };

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

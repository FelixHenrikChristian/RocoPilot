using Microsoft.Extensions.Logging;
using RocoPilot.Configuration;
using RocoPilot.Contracts.Services.Encounters;
using RocoPilot.Contracts.Services.ImageMatching;
using RocoPilot.Contracts.Services.Spirits;
using RocoPilot.Contracts.Services.TextRecognition;
using RocoPilot.Models.Capture;
using RocoPilot.Models.ImageMatching;
using RocoPilot.Models.Recognition;

namespace RocoPilot.Services.IndependentTasks;

internal enum FlowerSeedScene { Unknown, World, Manual, ChallengePage, FlowerList, Map, Interaction, Confirmation, Preparation, Battle }
internal sealed record FlowerSeedRow(string Name, ImageMatchResult Button, string RawName = "")
{
    public int Number { get; set; }
    public bool HasMatched { get; set; }
}
internal sealed record FlowerSeedScreen(FlowerSeedScene Scene, IReadOnlyList<FlowerSeedRow> Rows,
    ImageMatchResult? Button = null, string Text = "");

/// <summary>界面用全局默认模板算法定位，名称用默认 OCR 识别后复用当前图鉴匹配。</summary>
public sealed class FlowerSeedScreenRecognizer(IImageMatchingService images, ITextRecognitionService ocr,
    ISpiritCatalogService catalog, IEncounterSeasonConfigService seasons, ILogger<FlowerSeedScreenRecognizer> logger)
{
    internal async Task<FlowerSeedScreen> ReadAsync(CapturedFrame frame, int clientX, int clientY,
        int width, int height, CancellationToken cancellationToken, Action<RecognitionRegion, string>? reportOcr = null)
    {
        var scale = height / 1152d;
        var options = new ImageMatchOptions
        {
            MinimumScore = .94,
            TemplateScaleX = scale, TemplateScaleY = scale
        };
        var tab = await Match("flower-tab", .2, .015, .48, .1)
            ?? await Match("flower-tab-inactive", .2, .015, .48, .1);
        if (tab is not null)
        {
            if (!IsTabSelected(frame, tab)) return new(FlowerSeedScene.ChallengePage, [], tab);
            var buttons = await images.FindMatchesAsync(frame, Area(.78, .12, .94, .98),
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
            var challenge = await Match("confirmation-challenge", .5, .74, .72, .94);
            var cancel = await Match("confirmation-cancel", .3, .74, .52, .94);
            return challenge is not null && cancel is not null
                ? new(FlowerSeedScene.Confirmation, [], challenge) : new(FlowerSeedScene.Unknown, []);
        }
        // 标题只匹配共有的“花种”，兼容“命定花种”和“稀兽花种”。
        var map = await Match("map-header", .65, .015, .85, .09);
        if (map is not null)
        {
            var teleport = await Match("map-teleport", .68, .88, .92, .98);
            var name = await ReadName(Area(.72, .184, .87, .2274, "地图花种名称"));
            return new(FlowerSeedScene.Map, [], teleport, name.Name);
        }
        var preparation = await Match("preparation", .3, .3, .65, .45);
        if (preparation is not null)
            return new(FlowerSeedScene.Preparation, [], await Match("start", .49, .75, .69, .9));
        var prompt = await Match("interaction-f", .42, .24, .84, .9);
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
                return new(FlowerSeedScene.Interaction, [], Text: Normalize(text.Text));
            }
        }
        // 只采用游戏 HUD 与战斗按钮的正向匹配，未知画面不会由上一输入推断。
        var world = await images.MatchAsync(frame, Area(.08, .02, .2, .09), "2048x1152/magic-point.png", options, cancellationToken);
        if (world.IsMatch) return new(FlowerSeedScene.World, []);
        foreach (var template in new[] { "battle-button-skill", "battle-button-change", "battle-chat" })
        {
            var battle = await images.MatchAsync(frame, Area(.68, .62, 1, 1), "2048x1152/" + template + ".png", options, cancellationToken);
            if (battle.IsMatch) return new(FlowerSeedScene.Battle, []);
        }
        return new(FlowerSeedScene.Unknown, []);

        RecognitionRegion Area(double left, double top, double right, double bottom, string id = "") => new()
        {
            Id = id,
            X = clientX + (int)Math.Round(width * left), Y = clientY + (int)Math.Round(height * top),
            Width = Math.Max(1, (int)Math.Round(width * (right - left))), Height = Math.Max(1, (int)Math.Round(height * (bottom - top)))
        };
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

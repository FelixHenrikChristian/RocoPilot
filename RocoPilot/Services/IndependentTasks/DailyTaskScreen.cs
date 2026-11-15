using RocoPilot.Contracts.Services.ImageMatching;
using RocoPilot.Models.Capture;
using RocoPilot.Models.ImageMatching;
using RocoPilot.Models.Recognition;
using RocoPilot.Services.Recognition;

namespace RocoPilot.Services.IndependentTasks;

internal sealed class DailyTaskScreen
{
    private readonly CapturedFrame _frame;
    private readonly CaptureTargetWindow _window;
    private readonly IImageMatchingService _images;
    private readonly int _x, _y, _width, _height;
    public double Scale { get; }

    public DailyTaskScreen(CapturedFrame frame, CaptureTargetWindow window, IImageMatchingService images)
    {
        _frame = frame;
        _window = window;
        _images = images;
        RecognitionRegionImageHelper.TryGetClientAreaInCapturedFrame(frame, window, out _x, out _y, out _width, out _height);
        Scale = Math.Min(_width / 2048d, _height / 1152d);
    }

    public int ClientX(double px) => (int)Math.Round((px - _x) * (_window.ClientWidth > 0 ? _window.ClientWidth : _width) / _width);
    public int ClientY(double py) => (int)Math.Round((py - _y) * (_window.ClientHeight > 0 ? _window.ClientHeight : _height) / _height);

    public Task<ImageMatchResult?> Map(CancellationToken token) => Find("stamina-crafting/map", 45, 0, 320, 120, token);
    public Task<ImageMatchResult?> Garden(CancellationToken token) => Find("stamina-crafting/garden", 185, 36, 405, 103, token);

    public async Task<(bool Selected, ImageMatchResult? Target)> SelectGarden(CancellationToken token)
    {
        if (await Map(token) is null) return (false, null);
        var option = await Find("stamina-crafting/garden-option", 100, 300, 485, 1045, token, .90)
            ?? await Find("stamina-crafting/garden-option-selected", 100, 300, 485, 1045, token, .90);
        if (option != null || await Garden(token) != null) return (true, option);
        return (false, await Find("stamina-crafting/triangle", 180, 1010, 500, 1120, token));
    }

    public RecognitionRegion Region(int left, int top, int right, int bottom, RecognitionAnchor anchor = RecognitionAnchor.Auto) =>
        RecognitionRegionImageHelper.MapReferenceArea(_x, _y, _width, _height, 2048, 1152,
            left, top, right - left, bottom - top, anchor);

    public async Task<ImageMatchResult?> Find(string template, int left, int top, int right, int bottom,
        CancellationToken token, double threshold = .94, double templateScale = 1, RecognitionAnchor anchor = RecognitionAnchor.Auto)
    {
        var result = await _images.MatchAsync(_frame, Region(left, top, right, bottom, anchor), $"2048x1152/{template}.png",
            new ImageMatchOptions { MinimumScore = threshold, TemplateScaleX = Scale * templateScale, TemplateScaleY = Scale * templateScale }, token);
        return result.IsMatch ? result : null;
    }

    public Task<ImageMatchResult?> World(CancellationToken token) => Find("magic-point", 155, 20, 410, 104, token);
    public Task<ImageMatchResult?> Teleport(CancellationToken token) => Find("flower-seed/map-teleport", 1330, 985, 1940, 1145, token);
}

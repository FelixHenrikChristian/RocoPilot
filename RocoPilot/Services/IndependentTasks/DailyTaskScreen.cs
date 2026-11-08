using RocoPilot.Contracts.Services.ImageMatching;
using RocoPilot.Models.Capture;
using RocoPilot.Models.ImageMatching;
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

    public async Task<ImageMatchResult?> Find(string template, int left, int top, int right, int bottom,
        CancellationToken token, double threshold = .94)
    {
        var region = RecognitionRegionImageHelper.MapReferenceArea(_x, _y, _width, _height, 2048, 1152,
            left, top, right - left, bottom - top);
        var result = await _images.MatchAsync(_frame, region, $"2048x1152/{template}.png",
            new ImageMatchOptions { MinimumScore = threshold, TemplateScaleX = Scale, TemplateScaleY = Scale }, token);
        return result.IsMatch ? result : null;
    }

    public Task<ImageMatchResult?> World(CancellationToken token) => Find("magic-point", 155, 20, 410, 104, token);
    public Task<ImageMatchResult?> Teleport(CancellationToken token) => Find("flower-seed/map-teleport", 1330, 985, 1940, 1145, token);
}

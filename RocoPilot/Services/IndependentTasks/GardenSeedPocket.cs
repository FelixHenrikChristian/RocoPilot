using RocoPilot.Models.Capture;
using RocoPilot.Models.ImageMatching;
using RocoPilot.Models.Recognition;
using RocoPilot.Models.Runtime;

namespace RocoPilot.Services.IndependentTasks;

internal sealed class GardenSeedPocket(CapturedFrame frame, DailyTaskScreen screen)
{
    private const RecognitionAnchor Center = RecognitionAnchor.Center;

    private Task<ImageMatchResult?> Find(string icon, int x, int y, int right, int bottom, CancellationToken token, double score = .94) =>
        screen.Find("gardening/seed-pocket/" + icon, x, y, right, bottom, token, score, anchor: Center);

    public Task<ImageMatchResult?> Title(CancellationToken token) => Find("title", 912, 238, 1130, 310, token);
    public Task<ImageMatchResult?> Equip(CancellationToken token) => Find("equip", 1310, 823, 1452, 917, token);
    public Task<ImageMatchResult?> Remove(CancellationToken token) => Find("remove", 1310, 823, 1452, 917, token);

    public Task<ImageMatchResult?> Seed(int index, CancellationToken token)
    {
        int x = 484 + index % 5 * 137;
        return Find(IndependentTaskSettings.SaleFlowers[index], x - 52, 341, x + 52, 917, token, .982);
    }

    public Task<ImageMatchResult?> Detail(int index, CancellationToken token) =>
        screen.Find($"gardening/seed-pocket/{IndependentTaskSettings.SaleFlowers[index]}", 1300, 379, 1468, 531, token, .98, 1.67, Center);

    public (int X, int Y) ClientPoint(int x, int y)
    {
        var point = screen.Region(x, y, x + 1, y + 1, Center);
        return (screen.ClientX(point.X), screen.ClientY(point.Y));
    }

    public bool QuantitySelected(int quantity)
    {
        var x = 1176 + (quantity - 1) * 158;
        var point = screen.Region(x, 577, x + 1, 578, Center);
        return frame.Pixels[(point.Y * frame.Width + point.X) * 4] > 204;
    }
}

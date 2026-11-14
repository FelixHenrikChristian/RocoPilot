using RocoPilot.Models.Capture;
using RocoPilot.Services.Recognition;

namespace RocoPilot.Services.IndependentTasks;

// Positions are expressed in plot widths/heights from the north-west corner of the soil.
internal readonly record struct GardenPosition(double Column, double Row)
{
    public string? DirectionTo(double column, double row)
    {
        if (Math.Abs(Column - column) > .32) return Column < column ? "D" : "A";
        if (Math.Abs(Row - row) > .32) return Row < row ? "S" : "W";
        return null;
    }
}

internal sealed class GardenVision
{
    private readonly CapturedFrame _frame;
    private readonly int _x, _y, _width, _height;
    private readonly double _scale;

    public GardenVision(CapturedFrame frame, CaptureTargetWindow window)
    {
        _frame = frame;
        RecognitionRegionImageHelper.TryGetClientAreaInCapturedFrame(frame, window, out _x, out _y, out _width, out _height);
        _scale = Math.Min(_width / 2048d, _height / 1152d);
    }

    private (int R, int G, int B) Pixel(int x, int y)
    {
        var i = (y * _frame.Width + x) * 4;
        return (_frame.Pixels[i + 2], _frame.Pixels[i + 1], _frame.Pixels[i]);
    }

    public GardenPosition? Locate()
    {
        const int size = 179, center = 89, soilWidth = 74, soilHeight = 44;
        var cols = new int[size];
        var rows = new int[size];
        var cx = _x + _width - 121 * _scale;
        var cy = _y + 137 * _scale;
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var dx = x - center;
            var dy = y - center;
            if (dx * dx + dy * dy > 86 * 86) continue;
            var (r, g, b) = Pixel((int)Math.Round(cx + dx * _scale), (int)Math.Round(cy + dy * _scale));
            if (r is > 128 and < 238 && r - g is > 7 and < 41 && g - b is > 18 and < 68)
            {
                cols[x]++;
                rows[y]++;
            }
        }

        // Ignore isolated patches of the same colour outside the rectangular field.
        static int? Edge(int[] counts, int span, int minimum)
        {
            var best = 0;
            int? edge = null;
            for (var i = 0; i + span <= counts.Length; i++)
            {
                var score = 0;
                for (var j = i; j < i + span; j++)
                    if (counts[j] >= minimum) score++;
                if (score <= best) continue;
                best = score;
                edge = i;
            }
            return best >= span - 5 ? edge : null;
        }
        if (Edge(cols, soilWidth, 11) is not { } left || Edge(rows, soilHeight, 19) is not { } top) return null;
        return new((center - left) * 5d / soilWidth, (center - top) * 3d / soilHeight);
    }

    // The white rim encloses a blue drop or orange bag. Probe at
    // multiple radii so that a distant bubble counts just as much as a nearby one.
    public bool HasCareBubble()
    {
        var area = RecognitionRegionImageHelper.MapReferenceArea(_x, _y, _width, _height, 2048, 1152,
            110, 115, 1740, 660);
        var stride = Math.Max(1, (int)(3 * _scale));
        bool Cream(int x, int y)
        {
            var (r, g, b) = Pixel(x, y);
            return r >= 218 && g >= 216 && b >= 204 && r - b is >= 0 and < 30 && Math.Abs(r - g) < 12;
        }
        for (var y = area.Y; y < area.Y + area.Height; y += stride)
        for (var x = area.X; x < area.X + area.Width; x += stride)
        {
            var (r, g, b) = Pixel(x, y);
            var ink = (r is > 45 and < 115 && g is > 127 and < 191 && b - g is > 22 and < 64 && g - r > 52)
                  || (r > 189 && g is > 98 and < 166 && b is > 41 and < 115 && r - g > 55);
            if (!ink) continue;
            for (var radius = 10; radius <= 44; radius += 3)
            {
                var d = (int)Math.Round(radius * _scale);
                if (Cream(x - d, y) && Cream(x + d, y) && Cream(x, y - d) && Cream(x, y + d)
                    && Cream(x - d * 3 / 4, y - d * 3 / 4) && Cream(x + d * 3 / 4, y + d * 3 / 4))
                    return true;
            }
        }
        return false;
    }
}


internal static class GardenPrompts
{
    public static async Task<bool> CanPlant(this DailyTaskScreen screen, CancellationToken token)
    {
        var label = await screen.Find("gardening/plant", 1240, 578, 1350, 635, token);
        if (label == null) return false;
        var key = await screen.Find("gardening/f-key", 1140, 578, 1230, 635, token, .90);
        return key != null && Math.Abs((key.Y + key.Height / 2d) - (label.Y + label.Height / 2d)) < 8 * screen.Scale;
    }

    public static async Task<bool> HasHarvestPrompt(this DailyTaskScreen screen, CancellationToken token)
    {
        foreach (var size in new[] { 1d, .77, 1.27, 1.58 })
            if (await screen.Find("gardening/harvest", 260, 150, 1650, 660, token, .92, size) != null) return true;
        return false;
    }

}

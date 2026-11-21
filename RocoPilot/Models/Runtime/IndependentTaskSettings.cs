namespace RocoPilot.Models.Runtime;

/// <summary>
/// 独立任务配置与本次扫描获得的花种选项。
/// </summary>
public sealed class IndependentTaskSettings
{
    public const int DefaultSaleQuantity = 36;
    public static IReadOnlyList<string> SaleFlowers { get; } = Array.AsReadOnly(new[]
    {
        "向阳花", "蜜黄菌", "星霜花", "幽幽草", "喷气菇", "伞伞菌", "喵喵草", "海神花", "天使草",
        "大嘴花", "紫雀花", "雪菇", "睡铃", "海桑花", "彩玉花", "短木莲", "荧光兰", "紫晶菇",
        "蓝掌", "花星角", "流星兰", "火焰花", "恶魔雪茄", "象牙花"
    });
    public bool ReuseEquippedSeed { get; set; } = true;
    public string GardenSeed { get; set; } = SaleFlowers[0];
    public int SeedsPerPlot { get; set; } = 1;

    public string SaleFlower { get; set; } = SaleFlowers[0];
    public int SaleQuantity { get; set; } = DefaultSaleQuantity;

    public const int MinimumRunCount = 1;
    public const int MaximumRunCount = 99;
    public const int DefaultLegendaryChallengeRunCount = 3;
    public const int DefaultLegendaryDifficulty = 3;
    public const int DefaultFlowerSeedRunCount = 6;

    public const int MinimumThrowBallMs = 100;
    public const int MaximumThrowBallHoldMs = 10000;
    public const int MaximumThrowBallIntervalMs = 600000;
    public const int DefaultThrowBallHoldMinMs = 800;
    public const int DefaultThrowBallHoldMaxMs = 1300;
    public const int DefaultThrowBallIntervalMinMs = 700;
    public const int DefaultThrowBallIntervalMaxMs = 900;
    public const int MaximumThrowBallDurationMinutes = 480;
    public const int DefaultThrowBallDurationMinutes = 0;

    public static IReadOnlyList<string> LegendaryTargets { get; } = Array.AsReadOnly(new[] { "圣羽翼王", "龙息帕尔" });

    public string LegendaryTargetName { get; set; } = LegendaryTargets[0];
    public int LegendaryDifficulty { get; set; } = DefaultLegendaryDifficulty;
    public bool UseLegendaryMarkWhenInsufficient { get; set; } = true;

    public int LegendaryChallengeRunCount
    {
        get;
        set;
    } = DefaultLegendaryChallengeRunCount;

    public int FlowerSeedRunCount { get; set; } = DefaultFlowerSeedRunCount;

    public int FlowerSeedTargetNumber { get; set; }

    /// <summary>花种挑战：已手动传送到目标场景，跳过列表导航与地图传送。</summary>
    public bool FlowerSeedAtTargetLocation { get; set; }

    /// <summary>花种挑战：捕捉前优先使用凝练印记（紫色星星）；不足时改用契约印记。</summary>
    public bool FlowerSeedPreferCondensedMark { get; set; } = true;

    /// <summary>传说精灵挑战：已手动传送到目标场景，跳过列表导航与地图传送。</summary>
    public bool LegendaryAtTargetLocation { get; set; }

    /// <summary>自动丢球：按住左键时长的随机范围（毫秒）。</summary>
    public int ThrowBallHoldMinMs { get; set; } = DefaultThrowBallHoldMinMs;
    public int ThrowBallHoldMaxMs { get; set; } = DefaultThrowBallHoldMaxMs;

    /// <summary>自动丢球：两次丢球之间等待时长的随机范围（毫秒）。</summary>
    public int ThrowBallIntervalMinMs { get; set; } = DefaultThrowBallIntervalMinMs;
    public int ThrowBallIntervalMaxMs { get; set; } = DefaultThrowBallIntervalMaxMs;

    /// <summary>自动丢球：本轮任务时长（分钟）；0 表示不限时，直到手动停止。</summary>
    public int ThrowBallDurationMinutes { get; set; } = DefaultThrowBallDurationMinutes;

    public List<FlowerSeedOption> FlowerSeedOptions { get; set; } = [];

    public static IndependentTaskSettings CreateDefault()
    {
        return new IndependentTaskSettings();
    }

    public IndependentTaskSettings Clone()
    {
        return new IndependentTaskSettings
        {
            ReuseEquippedSeed = ReuseEquippedSeed,
            GardenSeed = GardenSeed,
            SeedsPerPlot = SeedsPerPlot,
            SaleFlower = SaleFlower,
            SaleQuantity = SaleQuantity,
            LegendaryChallengeRunCount = LegendaryChallengeRunCount,
            LegendaryTargetName = LegendaryTargetName,
            LegendaryDifficulty = LegendaryDifficulty,
            UseLegendaryMarkWhenInsufficient = UseLegendaryMarkWhenInsufficient,
            FlowerSeedRunCount = FlowerSeedRunCount,
            FlowerSeedTargetNumber = FlowerSeedTargetNumber,
            FlowerSeedAtTargetLocation = FlowerSeedAtTargetLocation,
            FlowerSeedPreferCondensedMark = FlowerSeedPreferCondensedMark,
            LegendaryAtTargetLocation = LegendaryAtTargetLocation,
            ThrowBallHoldMinMs = ThrowBallHoldMinMs,
            ThrowBallHoldMaxMs = ThrowBallHoldMaxMs,
            ThrowBallIntervalMinMs = ThrowBallIntervalMinMs,
            ThrowBallIntervalMaxMs = ThrowBallIntervalMaxMs,
            ThrowBallDurationMinutes = ThrowBallDurationMinutes,
            FlowerSeedOptions = [.. FlowerSeedOptions]
        };
    }

    public IndependentTaskSettings Normalize(IReadOnlyList<string>? legendaryTargets = null)
    {
        var holdMin = Math.Clamp(ThrowBallHoldMinMs, MinimumThrowBallMs, MaximumThrowBallHoldMs);
        var holdMax = Math.Clamp(ThrowBallHoldMaxMs, MinimumThrowBallMs, MaximumThrowBallHoldMs);
        if (holdMin > holdMax) (holdMin, holdMax) = (holdMax, holdMin);
        var intervalMin = Math.Clamp(ThrowBallIntervalMinMs, MinimumThrowBallMs, MaximumThrowBallIntervalMs);
        var intervalMax = Math.Clamp(ThrowBallIntervalMaxMs, MinimumThrowBallMs, MaximumThrowBallIntervalMs);
        if (intervalMin > intervalMax) (intervalMin, intervalMax) = (intervalMax, intervalMin);
        return new IndependentTaskSettings
        {
            ReuseEquippedSeed = ReuseEquippedSeed,
            GardenSeed = GardenSeed,
            SeedsPerPlot = SeedsPerPlot,
            SaleFlower = SaleFlower,
            SaleQuantity = SaleQuantity,
            LegendaryChallengeRunCount = Math.Clamp(LegendaryChallengeRunCount, MinimumRunCount, MaximumRunCount),
            LegendaryTargetName = (legendaryTargets ?? LegendaryTargets).Contains(LegendaryTargetName)
                ? LegendaryTargetName : legendaryTargets is null ? LegendaryTargets[0] : string.Empty,
            LegendaryDifficulty = LegendaryDifficulty is >= 1 and <= 3 ? LegendaryDifficulty : DefaultLegendaryDifficulty,
            UseLegendaryMarkWhenInsufficient = UseLegendaryMarkWhenInsufficient,
            FlowerSeedRunCount = Math.Clamp(FlowerSeedRunCount, MinimumRunCount, MaximumRunCount),
            FlowerSeedTargetNumber = FlowerSeedTargetNumber,
            FlowerSeedAtTargetLocation = FlowerSeedAtTargetLocation,
            FlowerSeedPreferCondensedMark = FlowerSeedPreferCondensedMark,
            LegendaryAtTargetLocation = LegendaryAtTargetLocation,
            ThrowBallHoldMinMs = holdMin,
            ThrowBallHoldMaxMs = holdMax,
            ThrowBallIntervalMinMs = intervalMin,
            ThrowBallIntervalMaxMs = intervalMax,
            ThrowBallDurationMinutes = Math.Clamp(ThrowBallDurationMinutes, 0, MaximumThrowBallDurationMinutes),
            FlowerSeedOptions = [.. FlowerSeedOptions ?? []]
        };
    }
}

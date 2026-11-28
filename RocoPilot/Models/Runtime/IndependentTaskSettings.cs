namespace RocoPilot.Models.Runtime;

/// <summary>体力合成：在炼金釜制造赛季球，或转化炼金制造凝练印记。</summary>
public enum StaminaCraftingProductKind
{
    SeasonBall,
    CondensedMark
}

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

    /// <summary>体力合成：制造赛季球，或走转化炼金制造凝练印记。</summary>
    public StaminaCraftingProductKind StaminaCraftingProduct { get; set; } = StaminaCraftingProductKind.SeasonBall;

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
            StaminaCraftingProduct = StaminaCraftingProduct,
            FlowerSeedOptions = [.. FlowerSeedOptions]
        };
    }

    public IndependentTaskSettings Normalize(IReadOnlyList<string>? legendaryTargets = null)
    {
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
            StaminaCraftingProduct = Enum.IsDefined(StaminaCraftingProduct) ? StaminaCraftingProduct : StaminaCraftingProductKind.SeasonBall,
            FlowerSeedOptions = [.. FlowerSeedOptions ?? []]
        };
    }
}

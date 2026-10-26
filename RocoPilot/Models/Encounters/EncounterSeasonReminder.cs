namespace RocoPilot.Models.Encounters;

public sealed record EncounterSeasonReminder(string SeasonId, DateOnly EndDate)
{
    internal string DismissalKey => FormattableString.Invariant($"{SeasonId.ToUpperInvariant()}|{EndDate:yyyy-MM-dd}");

    public string SeasonSummary => FormattableString.Invariant(
        $"当前软件的赛季配置仅更新至 {SeasonId}，结束日期为 {EndDate.Year} 年 {EndDate.Month} 月 {EndDate.Day} 日。");

    public string ImpactMessage => "新赛季统计将记录在“新赛季”中，更新后由你确认归属。\n血脉识别仍需等待软件更新。";

    public string CatalogHint => "新精灵资料可尝试通过“同步图鉴数据”更新，\n具体收录情况取决于图鉴源。";

    public string Message => $"{SeasonSummary}\n{ImpactMessage}\n{CatalogHint}";
}

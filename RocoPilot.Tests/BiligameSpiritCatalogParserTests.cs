using Microsoft.VisualStudio.TestTools.UnitTesting;

using RocoPilot.Services.Spirits;

namespace RocoPilot.Tests;

[TestClass]
public sealed class BiligameSpiritCatalogParserTests
{
    private const string ListUrl = "https://wiki.biligame.com/rocom/%E7%B2%BE%E7%81%B5%E5%9B%BE%E9%89%B4";

    [TestMethod]
    public void ParsesNormalAndShinyAvatarLayers()
    {
        var states = BiligameSpiritCatalogParser.ParseListPage(BuildFixture(), ListUrl);

        var shiny = states.Single(state => state.Item.Name == "恶魔叮").Item;
        Assert.IsTrue(shiny.HasShiny);
        Assert.AreEqual(
            "https://patchwiki.biligame.com/images/rocom/thumb/7/70/normal.png/180px-normal.png",
            shiny.AvatarUrl);
        Assert.AreEqual(
            "https://patchwiki.biligame.com/images/rocom/7/70/normal.png",
            shiny.OriginalImageUrl);
        Assert.AreEqual(
            "https://patchwiki.biligame.com/images/rocom/thumb/b/bf/shiny.png/180px-shiny.png",
            shiny.ShinyAvatarUrl);
        Assert.AreEqual(
            "https://patchwiki.biligame.com/images/rocom/b/bf/shiny.png",
            shiny.ShinyOriginalImageUrl);

        var normal = states.Single(state => state.Item.Name == "叮叮恶魔").Item;
        Assert.IsFalse(normal.HasShiny);
        Assert.AreEqual(string.Empty, normal.ShinyAvatarUrl);
        Assert.AreEqual(string.Empty, normal.ShinyOriginalImageUrl);
    }

    [TestMethod]
    public void RejectsShinyFlagWithoutShinyAvatarLayer()
    {
        var fixture = BuildFixture()
            .Replace(
                "<span class=\"dex-pet-art-layer dex-pet-art-shiny\"><img src=\"https://patchwiki.biligame.com/images/rocom/thumb/b/bf/shiny.png/180px-shiny.png\" /></span>",
                string.Empty,
                StringComparison.Ordinal);

        var exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => BiligameSpiritCatalogParser.ParseListPage(fixture, ListUrl));

        StringAssert.Contains(exception.Message, "异色标记与异色头像不一致");
    }

    private static string BuildFixture()
    {
        return """
            <div class="dex-count-note">共 <strong>2</strong> 个精灵</div>
            <div class="divsort dex-card dex-pet-card dex-card-shiny"
                 data-param1="一阶"
                 data-param2="恶"
                 data-param3="翼"
                 data-param4="原始形态"
                 data-param5="主形态"
                 data-param6="是">
                <div class="dex-card-kicker">NO.030<span>一阶</span></div>
                <div class="dex-card-name"><a href="/rocom/恶魔叮" title="恶魔叮">恶魔叮</a></div>
                <div class="dex-pet-art">
                    <span class="dex-pet-art-layer dex-pet-art-normal"><img src="https://patchwiki.biligame.com/images/rocom/thumb/7/70/normal.png/180px-normal.png" /></span>
                    <span class="dex-pet-art-layer dex-pet-art-shiny"><img src="https://patchwiki.biligame.com/images/rocom/thumb/b/bf/shiny.png/180px-shiny.png" /></span>
                </div>
            </div>
            <div class="divsort dex-card dex-pet-card"
                 data-param1="二阶"
                 data-param2="恶"
                 data-param3="翼"
                 data-param4="原始形态"
                 data-param5="主形态"
                 data-param6="否">
                <div class="dex-card-kicker">NO.031<span>二阶</span></div>
                <div class="dex-card-name"><a href="/rocom/叮叮恶魔" title="叮叮恶魔">叮叮恶魔</a></div>
                <div class="dex-pet-art">
                    <span class="dex-pet-art-layer dex-pet-art-normal"><img src="https://patchwiki.biligame.com/images/rocom/thumb/2/2b/normal2.png/180px-normal2.png" /></span>
                </div>
            </div>
            """;
    }
}

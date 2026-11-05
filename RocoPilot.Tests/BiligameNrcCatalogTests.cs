using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using RocoPilot.Models.Spirits;
using RocoPilot.Services.Spirits;
using RocoPilot.Services.Statistics;
using RocoPilot.Tests.TestDoubles;

namespace RocoPilot.Tests;

[TestClass]
public sealed class BiligameNrcCatalogTests
{
    internal const string ListUrl = "https://wiki.biligame.com/nrc/%E7%B2%BE%E7%81%B5%E5%9B%BE%E9%89%B4";
    internal static string Markup => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "biligame-nrc-catalog.html"));

    [TestMethod]
    public void ParsesNewCardsAndGroupsFormsByCatalogNumber()
    {
        var states = BiligameSpiritCatalogParser.ParseListPage(Markup, ListUrl);
        Assert.AreEqual(10, states.Count);
        Assert.AreEqual(10, BiligameSpiritCatalogParser.ParseReportedCount(Markup));
        var document = SpiritCatalogService.BuildDocument(new("biligame", "新版 Wiki", ListUrl), states);
        Assert.AreEqual(8, document.Count);
        var wolf = document.Spirits.Single(item => item.Name == "银月狼王");
        Assert.AreEqual("445", wolf.Id);
        Assert.AreEqual("443", wolf.BaseId);
        Assert.AreEqual("诅咒狼灵", wolf.BaseName);
        Assert.AreEqual("Ⅲ阶", wolf.Stage);
        Assert.AreEqual("幽", wolf.PrimaryAttribute);
        Assert.AreEqual("幻", wolf.SecondaryAttribute);
        StringAssert.Contains(wolf.PageUrl, "/nrc/");
        CollectionAssert.AreEqual(new[] { "诅咒狼灵", "新月狼灵", "银月狼王" }, wolf.EvolutionChainNames);
        Assert.AreEqual("果实立方人", document.Spirits.Single(item => item.Id == "466").BaseName);
    }

    [TestMethod]
    public void KeepsRegionalAndLordFormsAndShinyPortraits()
    {
        var states = BiligameSpiritCatalogParser.ParseListPage(Markup, ListUrl);
        var primary = states.Single(state => state.Item.Id == "011" && state.IsPrimaryForm);
        Assert.AreEqual("地区形态", primary.Item.Form);
        var regional = states.Single(state => state.Item.Name == "鸭吉吉（紧实的样子）").Item;
        Assert.AreEqual("鸭吉吉", regional.WikiName);
        Assert.AreEqual("紧实的样子", regional.RegionalForm);
        Assert.AreEqual(primary.Item.BaseName, regional.BaseName);
        Assert.IsTrue(regional.Aliases.Contains("鸭吉吉(紧实的样子)"));
        var lord = states.Single(state => state.Item.Id == "011" && state.Item.Form == "首领形态").Item;
        Assert.AreEqual(primary.Item.BaseName, lord.BaseName);
        var mushroom = states.Single(state => state.Item.Name == "小灵菇").Item;
        Assert.IsTrue(mushroom.HasShiny);
        Assert.AreEqual("https://patchwiki.biligame.com/images/nrc/3/3f/8v5u9w3ug87kparh17mcxa0ul4knv2r.png", mushroom.AvatarUrl);
        Assert.AreEqual("https://patchwiki.biligame.com/images/nrc/0/06/70ljrkl0xbh69svcx9gh3j0x9kss8fb.png", mushroom.ShinyAvatarUrl);
        Assert.AreEqual(mushroom.AvatarUrl, mushroom.OriginalImageUrl);
        Assert.IsFalse(states.Single(state => state.Item.Id == "443").Item.HasShiny);
    }

    [TestMethod]
    public void HandlesAdditionalClassesAndSingleQuotedAttributes()
    {
        var markup = Markup.Replace("class=\"npc-card\"", "class='extra npc-card selected'");
        var states = BiligameSpiritCatalogParser.ParseListPage(markup, ListUrl);
        Assert.AreEqual(10, states.Count);
    }

    [TestMethod]
    public void Season5CardsSkipUnknownPlaceholdersAndKeepEvolutionAndForms()
    {
        var markup = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "biligame-nrc-season5.html"));
        var states = BiligameSpiritCatalogParser.ParseListPage(markup, ListUrl);
        Assert.AreEqual(9, states.Count);
        Assert.AreEqual(BiligameSpiritCatalogParser.ParseReportedCount(markup), states.Count);
        var document = SpiritCatalogService.BuildDocument(new("biligame", "新版 Wiki", ListUrl), states);
        Assert.AreEqual(7, document.Count);
        var index = new SpiritCatalogIndex(document);
        Assert.AreEqual("小冥龙", index.ResolveEvolutionRecordName("冥古龙"));
        Assert.AreEqual("乐乐鲨", index.ResolveEvolutionRecordName("阿瓦鲨（虎纹鲨的样子）"));
        Assert.AreEqual("小剪影", index.ResolveEvolutionRecordName("影子剪纸"));
        Assert.AreEqual("阿布", index.ResolveEvolutionRecordName("超能阿布"));
        Assert.IsFalse(document.Spirits.Any(item => int.Parse(item.Id) >= 494));
        Assert.IsTrue(document.Spirits.Single(item => item.Name == "小剪影").HasShiny);
        Assert.IsFalse(document.Spirits.Single(item => item.Name == "小冥龙").HasShiny);
    }

    [TestMethod]
    [DataRow("冥古龙", "小冥龙")]
    [DataRow("地绡银龙", "地银链")]
    [DataRow("龙嵴金脉", "沉金兽")]
    [DataRow("阿瓦鲨（虎纹鲨的样子）", "乐乐鲨", "阿瓦鲨")]
    [DataRow("鸡麻麻", "蛋包蛋")]
    [DataRow("沙咔蟹", "蟹蟹椰")]
    [DataRow("圆环鲸羽", "鲸羽")]
    [DataRow("刨雪鹅", "豆粒鹅")]
    [DataRow("冬虫夏瓜", "西瓜虫")]
    [DataRow("影子剪纸", "小剪影")]
    [DataRow("滴廿豚", "滴溜豚")]
    [DataRow("超能阿布", "阿布")]
    public void BundledSeason5CatalogRecognizesNamesAndResolvesBaseForms(string name, string baseName, string? matchedName = null)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Configuration", "Spirits", "Sources", "biligame", "spirits.json");
        var document = JsonSerializer.Deserialize<SpiritCatalogDocument>(File.ReadAllText(path),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var index = new SpiritCatalogIndex(document);
        Assert.AreEqual(matchedName ?? name, index.Match(name, 1));
        Assert.AreEqual(baseName, index.ResolveEvolutionRecordName(name));
        var item = document.Spirits.Single(item => item.Name == name);
        Assert.IsTrue(File.Exists(Path.Combine(AppContext.BaseDirectory, item.AvatarPath)));
        if (item.HasShiny)
            Assert.IsTrue(File.Exists(Path.Combine(AppContext.BaseDirectory, item.ShinyAvatarPath)));
    }

    [TestMethod]
    [DataRow("data-number=\"443\"", "data-number=\"\"")]
    [DataRow("data-form=\"main\"", "data-form=\"unknown\"")]
    [DataRow("data-form=\"main|regional\"", "data-form=\"regional\"")]
    [DataRow("npc-art-normal", "missing-portrait")]
    [DataRow("npc-art-shiny", "missing-shiny")]
    public void RejectsIncompleteCardsInsteadOfDroppingThem(string before, string after)
    {
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            BiligameSpiritCatalogParser.ParseListPage(Markup.Replace(before, after), ListUrl));
    }

    [TestMethod]
    public async Task NewCatalogResolvesTemporaryNameWithoutAutomaticCounting()
    {
        var config = new TemporarySeasonConfig();
        var statistics = new StatisticsService(new ControlledSettingsStore(), NullLogger<StatisticsService>.Instance, config);
        await statistics.AddAccountAsync("100");
        await statistics.RecordTemporaryEncounterAsync("100", "new-wolf", "银月狼王", "",
            new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.FromHours(8)));
        var document = SpiritCatalogService.BuildDocument(new("biligame", "新版 Wiki", ListUrl),
            BiligameSpiritCatalogParser.ParseListPage(Markup, ListUrl));
        Assert.AreEqual(1, await statistics.RefreshTemporaryNamesAsync(document));
        var encounter = statistics.CurrentDocument.Accounts.Single().Temporary!.Encounters.Single();
        Assert.AreEqual("诅咒狼灵", encounter.Name);
        Assert.AreEqual(1, encounter.Count);
        Assert.AreEqual(0, await statistics.RefreshTemporaryNamesAsync(document));
    }
}

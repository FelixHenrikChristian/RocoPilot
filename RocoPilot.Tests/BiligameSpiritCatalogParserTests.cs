using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Services.Spirits;

namespace RocoPilot.Tests;

[TestClass]
public sealed class BiligameSpiritCatalogParserTests
{
    [TestMethod]
    public void ParsesNrcPetCatalogCards()
    {
        const string url = "https://wiki.biligame.com/nrc/%E7%B2%BE%E7%81%B5%E5%9B%BE%E9%89%B4";
        var html = "<div class=\"nrc-pet-catalog\"><div class=\"npc-total-number\">1</div>" +
            "<div class=\"npc-card\" data-number=\"001\" data-form=\"main\" data-shiny=\"yes\" data-type=\"光\" data-position=\"initial|final\">" +
            "<span class=\"npc-card-target\"><a href=\"/nrc/迪莫\" title=\"迪莫\">001 迪莫</a></span>" +
            "<div class=\"npc-stage\">一阶</div><div class=\"npc-art-normal\"><img src=\"https://patchwiki.biligame.com/images/nrc/1/1a/normal.png\" /></div>" +
            "<div class=\"npc-art-shiny\"><img src=\"https://patchwiki.biligame.com/images/nrc/2/2b/shiny.png\" /></div>" +
            "<div class=\"npc-name\">迪莫</div><div class=\"npc-form\"></div></div></div>";

        var states = BiligameSpiritCatalogParser.ParseListPage(html, url);
        Assert.AreEqual(1, states.Count);
        Assert.AreEqual("001", states[0].Item.Id);
        Assert.AreEqual("迪莫", states[0].Item.Name);
        Assert.AreEqual("Ⅰ阶", states[0].Item.Stage);
        Assert.IsTrue(states[0].Item.HasShiny);
        Assert.AreEqual("光", states[0].Item.PrimaryAttribute);
        Assert.AreEqual(1, BiligameSpiritCatalogParser.ParseReportedCount(html));
    }
}

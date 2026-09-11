using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Models.Spirits;
using RocoPilot.Services.Spirits;

namespace RocoPilot.Tests;

[TestClass]
public sealed class SpiritCatalogIndexTests
{
    [TestMethod]
    public void MatchingUsesAliasesWikiNamesAndNormalizedVariants()
    {
        var index = new SpiritCatalogIndex(Document());
        Assert.AreEqual("小火苗", index.Match(" 小火苗（普通形态） ", 1));
        Assert.AreEqual("烈火王", index.Match("大火", 1));
        Assert.AreEqual("烈火王", index.Match("进化名", 1));
        Assert.AreEqual(string.Empty, index.Match("完全未知", 1));
        Assert.AreEqual(string.Empty, index.Match(" ", 0));
    }

    [TestMethod]
    public void EvolutionResolutionKeepsBaseFormWithinItsChain()
    {
        var index = new SpiritCatalogIndex(Document());
        Assert.AreEqual("小火苗", index.ResolveEvolutionRecordName("大火"));
        Assert.AreEqual("小火苗", index.ResolveEvolutionRecordName("烈火王"));
        Assert.AreEqual("未知", index.ResolveEvolutionRecordName("未知（特殊形态）"));
    }

    [TestMethod]
    public void SnapshotRemainsStableAfterInputAndReturnedDocumentsAreEdited()
    {
        var input = Document();
        var snapshot = new SpiritCatalogSnapshot(input);
        input.Spirits[0].Name = "已改变";
        input.Spirits[1].Aliases.Clear();
        var exported = snapshot.CreateDocument();
        exported.Source.Id = "changed";
        exported.Spirits.Clear();
        Assert.AreEqual(2, snapshot.CreateDocument().Spirits.Count);
        Assert.AreEqual("烈火王", snapshot.Index.Match("大火", 1));
        Assert.AreEqual("小火苗", snapshot.Index.ResolveEvolutionRecordName("烈火王"));
    }

    [TestMethod]
    public void EmptyCatalogOnlyReturnsQueryWhenThresholdIsDisabled()
    {
        var index = new SpiritCatalogIndex(new SpiritCatalogDocument());
        Assert.AreEqual("名字", index.Match(" 名字 ", 0));
        Assert.AreEqual(string.Empty, index.Match("名字", 0.5));
    }

    private static SpiritCatalogDocument Document() => new()
    {
        Spirits =
        [
            new() { Id = "1", Name = "小火苗", WikiName = "小火苗（普通形态）", BaseId = "1", BaseName = "小火苗", ChainId = "fire" },
            new() { Id = "2", Name = "进化名", WikiName = "烈火王", Aliases = ["大火"], BaseId = "1", BaseName = "小火苗", ChainId = "fire" }
        ]
    };
}

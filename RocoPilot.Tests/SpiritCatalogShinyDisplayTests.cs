using Microsoft.VisualStudio.TestTools.UnitTesting;

using RocoPilot.Models.Spirits;
using RocoPilot.Views.Windows;

namespace RocoPilot.Tests;

[TestClass]
public sealed class SpiritCatalogShinyDisplayTests
{
    [TestMethod]
    public void FiltersShinyVariantsByIdAndExactStage()
    {
        var target = CreateItem("031", "三阶", "原始形态");
        var sameStage = CreateItem("031", "三阶", "原始形态");
        var sameStageVariant = CreateItem("031", "三阶", "变种形态");
        var lowerStage = CreateItem("031", "二阶", "原始形态");
        var differentSpirit = CreateItem("032", "三阶", "原始形态");
        var missingAvatar = CreateItem("031", "三阶", "原始形态");
        missingAvatar.ShinyAvatarPath = string.Empty;

        var result = SpiritCatalogWindow.FilterShinyVariants(
            target,
            [sameStage, sameStageVariant, lowerStage, differentSpirit, missingAvatar]);

        CollectionAssert.AreEqual(
            new[] { sameStage, sameStageVariant },
            result.ToArray());
    }

    [TestMethod]
    public void ExcludesLeaderFormsFromShinyDisplay()
    {
        var leaderStage = CreateItem("031", "首领形态", "原始形态");
        var leaderForm = CreateItem("031", "三阶", "首领形态");

        Assert.IsFalse(SpiritCatalogWindow.CanShowShiny(leaderStage));
        Assert.IsFalse(SpiritCatalogWindow.CanShowShiny(leaderForm));
    }

    private static SpiritCatalogItem CreateItem(string id, string stage, string form)
    {
        return new SpiritCatalogItem
        {
            Id = id,
            Stage = stage,
            Form = form,
            HasShiny = true,
            ShinyAvatarPath = $"Avatars/{id}_shiny.png"
        };
    }
}

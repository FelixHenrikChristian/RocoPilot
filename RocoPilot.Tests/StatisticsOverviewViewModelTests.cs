using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Models.Encounters;
using RocoPilot.Models.Statistics;
using RocoPilot.ViewModels;

namespace RocoPilot.Tests;

[TestClass]
public sealed class StatisticsOverviewViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void SeasonReorderingPreservesSelectionsById()
    {
        var document = Document();
        var overview = new StatisticsOverviewViewModel();
        overview.ApplyDocument(document, "100", new EncounterSeasonConfig { CurrentSeasonId = "S1" });
        overview.SelectedShinyScopeIndex = 1;
        Assert.AreEqual("S1", overview.SelectedSeason?.Id);

        overview.ApplyDocument(document, "100", new EncounterSeasonConfig { CurrentSeasonId = "S2" });

        Assert.AreEqual("S2", overview.Seasons[0].Id);
        Assert.AreEqual("S1", overview.SelectedSeason?.Id);
        Assert.AreEqual("S1", overview.SelectedShinyScopeSeasonId);
        Assert.AreEqual("S1", overview.DefaultShinyAddSeasonId);
    }

    [TestMethod]
    public void RemovedSeasonFallsBackToFirstSeasonAndAllShinyScope()
    {
        var document = Document();
        var overview = new StatisticsOverviewViewModel();
        overview.ApplyDocument(document, "100", new EncounterSeasonConfig { CurrentSeasonId = "S1" });
        overview.SelectedShinyScopeIndex = 1;
        document.Accounts[0].Seasons.RemoveAll(item => item.Id == "S1");

        overview.ApplyDocument(document, "100", new EncounterSeasonConfig());

        Assert.AreEqual("S2", overview.SelectedSeason?.Id);
        Assert.AreEqual(0, overview.SelectedShinyScopeIndex);
        Assert.IsNull(overview.SelectedShinyScopeSeasonId);
    }

    [TestMethod]
    public void ListBindingFeedbackDuringRefreshCannotChangeSelection()
    {
        var overview = new StatisticsOverviewViewModel();
        var config = new EncounterSeasonConfig { CurrentSeasonId = "S1" };
        overview.ApplyDocument(Document(), "100", config);
        overview.SelectedSeasonIndex = 1;
        overview.SelectedShinyScopeIndex = 2;
        overview.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(overview.Seasons) or nameof(overview.ShinyScopes))
            {
                overview.SelectedSeasonIndex = -1;
                overview.SelectedSeasonIndex = 0;
                overview.SelectedShinyScopeIndex = 0;
            }
        };

        overview.ApplyDocument(Document(), "100", config);

        Assert.AreEqual("S2", overview.SelectedSeason?.Id);
        Assert.AreEqual("S2", overview.SelectedShinyScopeSeasonId);
    }

    [TestMethod]
    public void AccountSelectionAndRemovalRebuildAllDisplayedData()
    {
        var overview = new StatisticsOverviewViewModel();
        var document = Document();
        overview.ApplyDocument(document, "200", new EncounterSeasonConfig());
        Assert.AreEqual("200", overview.SelectedAccount?.Uid);
        Assert.AreEqual(0, overview.PendingShinyCount);
        Assert.AreEqual(0, overview.Seasons.Count);

        overview.ApplyDocument(document, "missing", new EncounterSeasonConfig());
        Assert.AreEqual("100", overview.SelectedAccount?.Uid);
        Assert.AreEqual(1, overview.PendingShinyCount);

        overview.ApplyDocument(new StatisticsDocument(), null, new EncounterSeasonConfig());
        Assert.IsNull(overview.SelectedAccount);
        Assert.AreEqual(0, overview.PendingShinyCount);
        Assert.AreEqual(string.Empty, overview.PendingEditor.Name);
        Assert.AreEqual(0d, overview.PendingEditor.EncounterCount);
    }

    [TestMethod]
    public void BackgroundRefreshPreservesManualDraftButUpdatesUneditedCount()
    {
        var document = Document();
        var overview = new StatisticsOverviewViewModel();
        overview.ApplyDocument(document, "100", new EncounterSeasonConfig());
        Assert.AreEqual(8d, overview.PendingEditor.EncounterCount);
        document.Accounts[0].Seasons[0].Encounters[0].Count = 9;
        overview.ApplyDocument(document, "100", new EncounterSeasonConfig());
        Assert.AreEqual(9d, overview.PendingEditor.EncounterCount);

        overview.PendingEditor.Name = "手动精灵";
        overview.PendingEditor.EncounterCount = 35;
        overview.ApplyDocument(Document(), "100", new EncounterSeasonConfig());

        Assert.AreEqual("手动精灵", overview.PendingEditor.Name);
        Assert.AreEqual(35d, overview.PendingEditor.EncounterCount);
        overview.PendingEditor.Name = "精灵";
        Assert.AreEqual(8d, overview.PendingEditor.EncounterCount);
    }

    [TestMethod]
    public void NewPendingCaptureResetsDraftEvenWhenItsNameIsUnchanged()
    {
        var document = Document();
        var overview = new StatisticsOverviewViewModel();
        overview.ApplyDocument(document, "100", new EncounterSeasonConfig());
        overview.PendingEditor.EncounterCount = 35;
        document.Accounts[0].PendingShinyCaptures[0].Id = "next";

        overview.ApplyDocument(document, "100", new EncounterSeasonConfig());

        Assert.AreEqual(8d, overview.PendingEditor.EncounterCount);
    }

    [TestMethod]
    public void SameCaptureIdInAnotherAccountDoesNotInheritDraft()
    {
        var overview = new StatisticsOverviewViewModel();
        var document = Document();
        overview.ApplyDocument(document, "100", new EncounterSeasonConfig());
        overview.PendingEditor.Name = "手动精灵";
        overview.PendingEditor.EncounterCount = 35;
        var otherAccount = Document().Accounts[0];
        otherAccount.Uid = "200";
        document.Accounts[1] = otherAccount;

        overview.ApplyDocument(document, "200", new EncounterSeasonConfig());

        Assert.AreEqual("精灵", overview.PendingEditor.Name);
        Assert.AreEqual(8d, overview.PendingEditor.EncounterCount);
    }

    [TestMethod]
    [DataRow(double.NaN, 0d)]
    [DataRow(double.NegativeInfinity, 0d)]
    [DataRow(-1d, 0d)]
    [DataRow(double.PositiveInfinity, (double)int.MaxValue)]
    [DataRow(3_000_000_000d, (double)int.MaxValue)]
    public void EncounterCountStaysWithinPersistableRange(double value, double expected)
    {
        var editor = new PendingShinyCaptureEditor { EncounterCount = value };
        Assert.AreEqual(expected, editor.EncounterCount);
    }

    internal static StatisticsDocument Document() => new()
    {
        Accounts =
        [
            new()
            {
                Uid = "100",
                Seasons =
                [
                    new()
                    {
                        Id = "S1",
                        Encounters = [new() { Name = "精灵", Count = 8, Season = "S1", LastCapturedAt = Now }]
                    },
                    new() { Id = "S2" }
                ],
                PendingShinyCaptures = [new() { Id = "pending", Name = "精灵", Season = "S1", DetectedAt = Now }]
            },
            new() { Uid = "200" }
        ]
    };
}

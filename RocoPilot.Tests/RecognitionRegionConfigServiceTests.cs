using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using RocoPilot.Configuration;
using RocoPilot.Models.Recognition;
using RocoPilot.Services;
using RocoPilot.Services.Recognition;

namespace RocoPilot.Tests;

[TestClass]
public sealed class RecognitionRegionConfigServiceTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void LegacyBloodlineRegionKeepsCoordinatesAndEnabledState(bool enabled)
    {
        var path = Path.GetTempFileName();
        try
        {
            var config = new RecognitionRegionConfig
            {
                Regions = [new() { Id = "battle-tip-encounter-s3", X = 123, Y = 456, Width = 600, Height = 50, Enabled = enabled }]
            };
            File.WriteAllText(path, JsonSerializer.Serialize(config, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var service = CreateService();
            var loaded = service.LoadFromPath(path);
            var region = loaded.Regions.Single();
            Assert.AreEqual(RecognitionRegionIds.BattleBloodlineTip, region.Id);
            Assert.AreEqual((123, 456, 600, 50), (region.X, region.Y, region.Width, region.Height));
            Assert.AreEqual(enabled, region.Enabled);
            Assert.AreEqual(enabled, EncounterBloodlineRecognition.IsAvailable(loaded));

            service.Save(loaded);
            var reloaded = service.LoadFromPath(path);
            Assert.AreEqual(RecognitionRegionIds.BattleBloodlineTip, reloaded.Regions.Single().Id);
            Assert.AreEqual(enabled, EncounterBloodlineRecognition.IsAvailable(reloaded));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void ExplicitlyDisabledNewRegionIsNotReenabledByLegacyRegion()
    {
        var path = Path.GetTempFileName();
        try
        {
            var config = new RecognitionRegionConfig
            {
                Regions =
                [
                    new() { Id = "battle-tip-encounter-s3", Width = 600, Height = 50 },
                    new() { Id = RecognitionRegionIds.BattleBloodlineTip, Width = 700, Height = 60, Enabled = false }
                ]
            };
            File.WriteAllText(path, JsonSerializer.Serialize(config, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var loaded = CreateService().LoadFromPath(path);
            Assert.IsFalse(EncounterBloodlineRecognition.IsAvailable(loaded));
            Assert.AreEqual(1, loaded.Regions.Count(region => region.Id == RecognitionRegionIds.BattleBloodlineTip));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    [DataRow(1920, 1440)]
    [DataRow(2048, 1152)]
    public void BundledRegionsEnableGenericBloodlineRecognition(int width, int height)
    {
        var config = CreateService().LoadForResolution(width, height);
        Assert.IsTrue(config.LoadedFromFile);
        Assert.IsTrue(EncounterBloodlineRecognition.IsAvailable(config));
        Assert.IsFalse(config.Regions.Any(region => region.Id == "battle-tip-encounter-s3"));
    }

    [TestMethod]
    [DataRow(0, 50)]
    [DataRow(600, 0)]
    public void MissingOrEmptyRegionDoesNotWaitForBloodlineOcr(int width, int height)
    {
        Assert.IsFalse(EncounterBloodlineRecognition.IsAvailable(new RecognitionRegionConfig()));
        var config = new RecognitionRegionConfig
        {
            Regions = [new() { Id = RecognitionRegionIds.BattleBloodlineTip, Width = width, Height = height }]
        };
        Assert.IsFalse(EncounterBloodlineRecognition.IsAvailable(config));
    }

    private static RecognitionRegionConfigService CreateService() => new(NullLogger<RecognitionRegionConfigService>.Instance);
}

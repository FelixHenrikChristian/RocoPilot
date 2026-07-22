using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using OpenCvSharp;

using RocoPilot.Contracts.Services;
using RocoPilot.Models.Capture;
using RocoPilot.Models.ImageMatching;
using RocoPilot.Models.Recognition;
using RocoPilot.Services.ImageMatching;

namespace RocoPilot.Tests;

[TestClass]
public sealed class ImageMatchingServiceTests
{
    [TestMethod]
    public async Task GlobalAlgorithmCanBeLoadedAndChanged()
    {
        var settings = new MemoryLocalSettingsService(ImageMatchAlgorithm.WeightedRgbError);
        var service = CreateService(settings);

        await service.InitializeAsync();

        Assert.AreEqual(ImageMatchAlgorithm.WeightedRgbError, service.DefaultAlgorithm);

        await service.SetDefaultAlgorithmAsync(ImageMatchAlgorithm.OpenCvSqDiffNormalized);

        Assert.AreEqual(ImageMatchAlgorithm.OpenCvSqDiffNormalized, service.DefaultAlgorithm);
        Assert.AreEqual(
            ImageMatchAlgorithm.OpenCvSqDiffNormalized,
            await settings.ReadSettingAsync<ImageMatchAlgorithm?>("ImageMatchAlgorithm"));
    }

    [TestMethod]
    public async Task BothAlgorithmsLocateTheSameTemplateAndRespectAlphaMask()
    {
        var fixture = CreateFixture([(3, 2)]);
        try
        {
            var service = CreateService(new MemoryLocalSettingsService());

            foreach (var algorithm in new[]
                     {
                         ImageMatchAlgorithm.WeightedRgbError,
                         ImageMatchAlgorithm.OpenCvSqDiffNormalized
                     })
            {
                var result = await service.MatchAsync(
                    fixture.Frame,
                    fixture.Region,
                    fixture.TemplatePath,
                    new ImageMatchOptions
                    {
                        Algorithm = algorithm,
                        MinimumScore = 0.99
                    });

                Assert.IsTrue(result.IsMatch, algorithm.ToString());
                Assert.AreEqual(3, result.X, algorithm.ToString());
                Assert.AreEqual(2, result.Y, algorithm.ToString());
                Assert.IsGreaterThanOrEqualTo(0.999, result.Score, algorithm.ToString());
            }
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [TestMethod]
    public async Task OpenCvAlgorithmFindsMultipleNonOverlappingMatches()
    {
        var fixture = CreateFixture([(2, 3), (9, 3)]);
        try
        {
            var service = CreateService(new MemoryLocalSettingsService());
            var result = await service.FindMatchesAsync(
                fixture.Frame,
                fixture.Region,
                fixture.TemplatePath,
                maximumMatches: 2,
                new ImageMatchOptions
                {
                    Algorithm = ImageMatchAlgorithm.OpenCvSqDiffNormalized,
                    MinimumScore = 0.99
                });

            Assert.HasCount(2, result.Matches);
            CollectionAssert.AreEquivalent(
                new[] { (2, 3), (9, 3) },
                result.Matches.Select(match => (match.X, match.Y)).ToArray());
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static ImageMatchingService CreateService(ILocalSettingsService settings)
    {
        return new ImageMatchingService(settings, NullLogger<ImageMatchingService>.Instance);
    }

    private static ImageMatchingFixture CreateFixture(IReadOnlyList<(int X, int Y)> origins)
    {
        const int templateWidth = 4;
        const int templateHeight = 3;
        const int frameWidth = 16;
        const int frameHeight = 9;
        var templatePixels = new byte[]
        {
            0, 0, 0, 0,       30, 80, 220, 255,  180, 20, 40, 255,   0, 0, 0, 0,
            90, 210, 40, 255,  10, 120, 250, 255, 60, 40, 190, 255,   210, 150, 20, 255,
            0, 0, 0, 0,       240, 60, 100, 255, 20, 230, 130, 255,  0, 0, 0, 0
        };
        var framePixels = new byte[frameWidth * frameHeight * 4];
        for (var offset = 0; offset < framePixels.Length; offset += 4)
        {
            framePixels[offset] = 12;
            framePixels[offset + 1] = 18;
            framePixels[offset + 2] = 24;
            framePixels[offset + 3] = 255;
        }

        foreach (var (originX, originY) in origins)
        {
            for (var y = 0; y < templateHeight; y++)
            {
                for (var x = 0; x < templateWidth; x++)
                {
                    var templateOffset = ((y * templateWidth) + x) * 4;
                    if (templatePixels[templateOffset + 3] == 0)
                    {
                        continue;
                    }

                    var frameOffset = (((originY + y) * frameWidth) + originX + x) * 4;
                    Array.Copy(templatePixels, templateOffset, framePixels, frameOffset, 4);
                }
            }
        }

        var directory = Path.Combine(Path.GetTempPath(), "RocoPilot.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var templatePath = Path.Combine(directory, "template.png");
        using (var image = new Mat(templateHeight, templateWidth, MatType.CV_8UC4))
        {
            for (var y = 0; y < templateHeight; y++)
            {
                for (var x = 0; x < templateWidth; x++)
                {
                    var offset = ((y * templateWidth) + x) * 4;
                    image.Set(y, x, new Vec4b(
                        templatePixels[offset],
                        templatePixels[offset + 1],
                        templatePixels[offset + 2],
                        templatePixels[offset + 3]));
                }
            }

            Cv2.ImWrite(templatePath, image);
        }

        return new ImageMatchingFixture(
            directory,
            templatePath,
            new CapturedFrame(frameWidth, frameHeight, framePixels),
            new RecognitionRegion
            {
                Id = "test",
                X = 0,
                Y = 0,
                Width = frameWidth,
                Height = frameHeight,
                Enabled = true
            });
    }

    private sealed record ImageMatchingFixture(
        string DirectoryPath,
        string TemplatePath,
        CapturedFrame Frame,
        RecognitionRegion Region) : IDisposable
    {
        public void Dispose()
        {
            Frame.Dispose();
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }

    private sealed class MemoryLocalSettingsService : ILocalSettingsService
    {
        private readonly Dictionary<string, object?> _values = new();

        public MemoryLocalSettingsService(ImageMatchAlgorithm? algorithm = null)
        {
            if (algorithm.HasValue)
            {
                _values["ImageMatchAlgorithm"] = algorithm.Value;
            }
        }

        public Task<T?> ReadSettingAsync<T>(string key)
        {
            return Task.FromResult(
                _values.TryGetValue(key, out var value)
                    ? (T?)value
                    : default);
        }

        public Task SaveSettingAsync<T>(string key, T value)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task ResetAllAsync()
        {
            _values.Clear();
            return Task.CompletedTask;
        }
    }
}

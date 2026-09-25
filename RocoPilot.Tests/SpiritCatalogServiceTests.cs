using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Models.Spirits;
using RocoPilot.Services.Spirits;
using RocoPilot.Tests.TestDoubles;

namespace RocoPilot.Tests;

[TestClass]
public sealed class SpiritCatalogServiceTests
{
    [TestMethod]
    public async Task SyncMigratesExistingBiligameCacheToNewWiki()
    {
        using var fixture = new Fixture { ListMarkup = BiligameNrcCatalogTests.Markup };
        Assert.AreEqual("旧精灵", (await fixture.Service.LoadAsync("biligame")).Spirits.Single().Name);
        var document = await fixture.Service.SyncAsync("biligame");
        Assert.AreEqual(BiligameNrcCatalogTests.ListUrl, fixture.LastListUri?.AbsoluteUri);
        Assert.AreEqual("biligame", document.Source.Id);
        Assert.AreEqual(BiligameNrcCatalogTests.ListUrl, document.Source.ListUrl);
        Assert.AreEqual(8, document.Count);
        Assert.AreEqual(10, document.Spirits.Count);
        var matchedName = await fixture.Service.MatchSpiritNameAsync("银月狼王", 1);
        Assert.AreEqual("银月狼王", matchedName);
        Assert.AreEqual("诅咒狼灵", await fixture.Service.ResolveEvolutionRecordNameAsync(matchedName));
        var saved = JsonSerializer.Deserialize<SpiritCatalogDocument>(await File.ReadAllTextAsync(fixture.DataPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.AreEqual(10, saved.Spirits.Count);
        Assert.AreEqual(BiligameNrcCatalogTests.ListUrl, saved.Source.ListUrl);
        Assert.IsTrue(saved.Spirits.All(item => File.Exists(fixture.Service.ResolveAvatarPath(item.AvatarPath))));
        Assert.AreEqual(string.Empty, await fixture.Service.MatchSpiritNameAsync("旧精灵", 1));
        Assert.IsFalse(File.Exists(fixture.OldAvatarPath));
        AssertCatalogFilesMatchDocument(fixture, saved);
    }

    [TestMethod]
    public async Task RepeatedNewWikiSyncDoesNotAccumulateCatalogOrAvatars()
    {
        using var fixture = new Fixture(withBundledCatalog: true) { ListMarkup = BiligameNrcCatalogTests.Markup };
        await fixture.Service.LoadAsync();
        var staleAvatar = Path.Combine(Path.GetDirectoryName(fixture.OldAvatarPath)!, "unused.png");
        await File.WriteAllTextAsync(staleAvatar, "unreferenced avatar");

        var first = await fixture.Service.SyncAsync();
        AssertCatalogFilesMatchDocument(fixture, first);
        Assert.IsFalse(File.Exists(staleAvatar));
        var oldMushroom = first.Spirits.Single(item => item.Name == "小灵菇");
        var oldNormalPath = fixture.Service.ResolveAvatarPath(oldMushroom.AvatarPath)!;
        var oldShinyPath = fixture.Service.ResolveAvatarPath(oldMushroom.ShinyAvatarPath)!;
        fixture.ListMarkup = fixture.ListMarkup
            .Replace(oldMushroom.AvatarUrl, "https://example.test/new-normal.png")
            .Replace(oldMushroom.ShinyAvatarUrl, "https://example.test/new-shiny.png");

        var updated = await fixture.Service.SyncAsync();
        AssertCatalogFilesMatchDocument(fixture, updated);
        Assert.IsFalse(File.Exists(oldNormalPath));
        Assert.IsFalse(File.Exists(oldShinyPath));
        var repeated = await fixture.Service.SyncAsync();
        Assert.AreEqual(8, repeated.Count);
        Assert.AreEqual(10, repeated.Spirits.Count);
        AssertCatalogFilesMatchDocument(fixture, repeated);

        using var restarted = fixture.CreateService();
        var reloaded = await restarted.LoadAsync();
        Assert.AreEqual(10, reloaded.Spirits.Count);
        Assert.AreEqual(BiligameNrcCatalogTests.ListUrl, reloaded.Source.ListUrl);
        Assert.AreEqual("https://example.test/new-normal.png", reloaded.Spirits.Single(item => item.Name == "小灵菇").AvatarUrl);
        Assert.AreEqual(string.Empty, await restarted.MatchSpiritNameAsync("旧精灵", 1));
        AssertCatalogFilesMatchDocument(fixture, reloaded);
    }

    private static void AssertCatalogFilesMatchDocument(Fixture fixture, SpiritCatalogDocument document)
    {
        var sourceDirectory = Path.GetDirectoryName(fixture.DataPath)!;
        CollectionAssert.AreEquivalent(new[] { sourceDirectory }, Directory.GetDirectories(Path.GetDirectoryName(sourceDirectory)!));
        CollectionAssert.AreEquivalent(new[] { fixture.DataPath }, Directory.GetFiles(sourceDirectory, "*.json"));
        var referencedAvatars = document.Spirits
            .SelectMany(item => new[] { item.AvatarPath, item.ShinyAvatarPath })
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => fixture.Service.ResolveAvatarPath(path)!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        CollectionAssert.AreEquivalent(referencedAvatars, Directory.GetFiles(Path.GetDirectoryName(fixture.OldAvatarPath)!));
        Assert.AreEqual(document.Spirits.Count, document.Spirits.Select(item => (item.Id, item.Name)).Distinct().Count());
    }

    [TestMethod]
    public async Task NewWikiCountMismatchKeepsExistingCache()
    {
        using var fixture = new Fixture
        {
            ListMarkup = BiligameNrcCatalogTests.Markup.Replace("npc-total-number\">10<", "npc-total-number\">11<")
        };
        await fixture.Service.LoadAsync();
        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Service.SyncAsync());
        StringAssert.Contains(exception.Message, "声明 11 张卡片，实际解析 10 张");
        Assert.AreEqual("旧精灵", await fixture.Service.MatchSpiritNameAsync("旧精灵", 1));
        StringAssert.Contains(await File.ReadAllTextAsync(fixture.DataPath), "旧精灵");
        Assert.IsTrue(File.Exists(fixture.OldAvatarPath));
        Assert.AreEqual(1, fixture.HttpRequests);
    }

    [TestMethod]
    public async Task ConcurrentCallersCannotMutateCachedDocumentOrIndex()
    {
        using var fixture = new Fixture();
        var tasks = Enumerable.Range(0, 30).Select(async _ =>
        {
            var document = await fixture.Service.LoadAsync();
            document.Spirits.Clear();
            document.Source.Id = "changed";
            Assert.AreEqual("旧精灵", await fixture.Service.MatchSpiritNameAsync("旧精灵", 1));
        });
        await Task.WhenAll(tasks);
        Assert.AreEqual(1, (await fixture.Service.LoadAsync()).Spirits.Count);
        Assert.AreEqual(0, fixture.HttpRequests);
    }

    [TestMethod]
    public async Task SyncPublishesNewDocumentAndIndexTogether()
    {
        using var fixture = new Fixture();
        await fixture.Service.LoadAsync();
        using var pause = new AsyncPause();
        fixture.BeforeList = () => pause.PauseAsync(string.Empty);
        var syncing = fixture.Service.SyncAsync();
        await pause.WaitUntilEnteredAsync();
        var loading = fixture.Service.LoadAsync();
        var matching = fixture.Service.MatchSpiritNameAsync("新精灵", 1);
        Assert.IsFalse(loading.IsCompleted);
        Assert.IsFalse(matching.IsCompleted);
        pause.Dispose();
        var synced = await syncing;
        synced.Spirits.Clear();
        Assert.AreEqual("新精灵", (await loading).Spirits.Single().Name);
        Assert.AreEqual("新精灵", await matching);
        Assert.AreEqual(string.Empty, await fixture.Service.MatchSpiritNameAsync("旧精灵", 1));
        Assert.AreEqual("新精灵", (await fixture.Service.LoadAsync()).Spirits.Single().Name);
    }

    [TestMethod]
    public async Task FailedPersistenceKeepsOldCatalogAndAvatarsUntilSuccessfulRetry()
    {
        using var fixture = new Fixture();
        await fixture.Service.LoadAsync();
        using (var locked = new FileStream(fixture.DataPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.ThrowsExactlyAsync<IOException>(() => fixture.Service.SyncAsync());
        Assert.IsTrue(File.Exists(fixture.OldAvatarPath));
        Assert.AreEqual("旧精灵", await fixture.Service.MatchSpiritNameAsync("旧精灵", 1));
        StringAssert.Contains(await File.ReadAllTextAsync(fixture.DataPath), "旧精灵");
        Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(fixture.DataPath)!, "*.tmp").Length);

        await fixture.Service.SyncAsync();

        Assert.IsFalse(File.Exists(fixture.OldAvatarPath));
        Assert.AreEqual("新精灵", await fixture.Service.MatchSpiritNameAsync("新精灵", 1));
        var item = (await fixture.Service.LoadAsync()).Spirits.Single();
        Assert.IsTrue(File.Exists(fixture.Service.ResolveAvatarPath(item.AvatarPath)));
    }

    [TestMethod]
    public async Task CancellationBeforeCommitKeepsOldCatalogAndAvatars()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        await fixture.Service.LoadAsync();
        var progress = new InlineProgress(value =>
        {
            if (value.Message == "正在写入图鉴数据") cancellation.Cancel();
        });
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Service.SyncAsync(progress, cancellation.Token));
        Assert.IsTrue(File.Exists(fixture.OldAvatarPath));
        Assert.AreEqual("旧精灵", (await fixture.Service.LoadAsync()).Spirits.Single().Name);
        StringAssert.Contains(await File.ReadAllTextAsync(fixture.DataPath), "旧精灵");
    }

    [TestMethod]
    public async Task MarkerFailureDoesNotHideCommittedCatalog()
    {
        using var fixture = new Fixture(withBundledCatalog: true);
        await fixture.Service.LoadAsync();
        var marker = Path.Combine(Path.GetDirectoryName(fixture.DataPath)!, "bundled-spirits.marker");
        using (var locked = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var document = await fixture.Service.SyncAsync();
            Assert.AreEqual("新精灵", document.Spirits.Single().Name);
        }
        Assert.AreEqual("新精灵", await fixture.Service.MatchSpiritNameAsync("新精灵", 1));
        StringAssert.Contains(await File.ReadAllTextAsync(fixture.DataPath), "新精灵");
    }

    private sealed class InlineProgress(Action<SpiritCatalogSyncProgress> report) : IProgress<SpiritCatalogSyncProgress>
    {
        public void Report(SpiritCatalogSyncProgress value) => report(value);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("RocoPilot-catalog-test-");
        public SpiritCatalogService Service { get; }
        public string DataPath { get; }
        public string OldAvatarPath { get; }
        public int HttpRequests;
        public Func<Task>? BeforeList { get; set; }
        public string ListMarkup { get; set; } = Markup;
        public Uri? LastListUri { get; private set; }

        public Fixture(bool withBundledCatalog = false)
        {
            var local = Path.Combine(_directory.FullName, "local");
            var bundled = Path.Combine(_directory.FullName, "bundled");
            DataPath = Path.Combine(local, "Spirits", "Sources", "biligame", "spirits.json");
            OldAvatarPath = Path.Combine(Path.GetDirectoryName(DataPath)!, "Avatars", "old.png");
            Directory.CreateDirectory(Path.GetDirectoryName(OldAvatarPath)!);
            File.WriteAllText(OldAvatarPath, "old avatar");
            var document = new SpiritCatalogDocument
            {
                Source = new() { Id = "biligame", Name = "Biligame 洛克王国:手游 Wiki 精灵图鉴", ListUrl = "https://wiki.biligame.com/rocom/精灵图鉴" },
                Spirits = [new() { Id = "1", Name = "旧精灵", WikiName = "旧精灵", AvatarPath = "Spirits/Sources/biligame/Avatars/old.png" }]
            };
            var json = JsonSerializer.Serialize(document, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            File.WriteAllText(DataPath, json);
            if (withBundledCatalog)
            {
                var path = Path.Combine(bundled, "Configuration", "Spirits", "Sources", "biligame", "spirits.json");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, json);
            }
            Service = CreateService();
        }

        public SpiritCatalogService CreateService() =>
            new(Path.Combine(_directory.FullName, "local"), Path.Combine(_directory.FullName, "bundled"), NullLogger<SpiritCatalogService>.Instance,
                new ControlledSettingsStore(), new HttpClient(new Handler(async request =>
                {
                    Interlocked.Increment(ref HttpRequests);
                    if (request.RequestUri!.Host != "wiki.biligame.com")
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(request.RequestUri.AbsoluteUri)) };
                    LastListUri = request.RequestUri;
                    if (BeforeList is { } beforeList) await beforeList();
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ListMarkup) };
                })));

        public void Dispose()
        {
            Service.Dispose();
            var path = Path.GetFullPath(_directory.FullName);
            if (!path.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !_directory.Name.StartsWith("RocoPilot-catalog-test-", StringComparison.Ordinal))
                throw new InvalidOperationException("测试目录不在预期路径内。");
            _directory.Delete(recursive: true);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request);
    }

    private const string Markup = """
        <div class="dex-count-note">共 <strong>1</strong> 个精灵</div>
        <div class="divsort dex-card dex-pet-card" data-param1="一阶" data-param4="原始形态" data-param5="主形态" data-param6="否">
            <div class="dex-card-kicker">NO.002<span>一阶</span></div>
            <div class="dex-card-name"><a href="/rocom/新精灵" title="新精灵">新精灵</a></div>
            <div class="dex-pet-art"><span class="dex-pet-art-layer dex-pet-art-normal"><img src="https://example.test/avatar.png" /></span></div>
        </div>
        """;
}

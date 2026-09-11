using System.Net;
using System.Net.Http.Headers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocoPilot.Models.Statistics;
using RocoPilot.Services.Statistics.Sync;

namespace RocoPilot.Tests;

[TestClass]
public sealed class S3StatisticsRemoteStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 1, 2, 3, TimeSpan.Zero);

    [TestMethod]
    public async Task ReadInfoSignsRequestAndReadsVersionMetadata()
    {
        var content = new TrackingContent("data");
        content.Headers.LastModified = Now.AddDays(-1);
        using var store = CreateStore(request =>
        {
            Assert.AreEqual(HttpMethod.Head, request.Method);
            Assert.AreEqual("https://account.r2.cloudflarestorage.com/bucket/statistics.json", request.RequestUri!.AbsoluteUri);
            Assert.AreEqual("20260912T010203Z", request.Headers.GetValues("x-amz-date").Single());
            StringAssert.Contains(request.Headers.GetValues("Authorization").Single(), "Credential=test-key/20260912/auto/s3/aws4_request");
            StringAssert.Contains(request.Headers.GetValues("Authorization").Single(), "Signature=55d262a48c8c406b3ff29aa1f0a54a6387ff496c31b69f03397c4cbb81d27270");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content, Headers = { ETag = new EntityTagHeaderValue("\"v1\"") } };
        });

        var info = await store.ReadInfoAsync(Settings(), "test-secret", default);

        Assert.IsTrue(info.Exists);
        Assert.AreEqual("v1", info.EntityTag);
        Assert.AreEqual(Now.AddDays(-1), info.LastModifiedAt);
        Assert.IsTrue(content.IsDisposed);
    }

    [TestMethod]
    public async Task ObjectPathEscapesSegmentsAndPreservesEndpointPrefix()
    {
        var settings = Settings();
        settings.Endpoint = "https://example.test:9000/root";
        settings.BucketName = "my bucket";
        settings.RemotePath = "folder\\统计 +.json";
        using var store = CreateStore(request =>
        {
            Assert.AreEqual("https://example.test:9000/root/my%20bucket/folder/%E7%BB%9F%E8%AE%A1%20%2B.json", request.RequestUri!.AbsoluteUri);
            Assert.AreEqual("example.test:9000", request.Headers.Host);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        Assert.IsFalse((await store.ReadInfoAsync(settings, "secret", default)).Exists);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Forbidden)]
    [DataRow(HttpStatusCode.NotFound)]
    public async Task FailedDownloadDisposesResponse(HttpStatusCode status)
    {
        var content = new TrackingContent("error");
        using var store = CreateStore(_ => new HttpResponseMessage(status) { Content = content });
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => store.DownloadAsync(Settings(), "secret", default));
        Assert.IsTrue(content.IsDisposed);
    }

    [TestMethod]
    public async Task InvalidDocumentDisposesResponse()
    {
        var content = new TrackingContent("{\"info\":{\"format\":\"unknown\"}}");
        using var store = CreateStore(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => store.DownloadAsync(Settings(), "secret", default));
        Assert.IsTrue(content.IsDisposed);
    }

    [TestMethod]
    public async Task DownloadReturnsDocumentAndMatchingResponseVersion()
    {
        var content = new TrackingContent("{\"info\":{\"format\":\"RocoPilot.Statistics\"},\"accounts\":[{\"uid\":\"100\"}]}");
        using var store = CreateStore(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
            Headers = { ETag = new EntityTagHeaderValue("\"downloaded\"") }
        });
        var downloaded = await store.DownloadAsync(Settings(), "secret", default);
        Assert.AreEqual("100", downloaded.Document.Accounts.Single().Uid);
        Assert.AreEqual("downloaded", downloaded.Info.EntityTag);
        Assert.IsTrue(content.IsDisposed);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UploadUsesExpectedVersionAndReportsConflict(bool exists)
    {
        var content = new TrackingContent("changed");
        using var store = CreateStore(request =>
        {
            Assert.AreEqual(HttpMethod.Put, request.Method);
            Assert.AreEqual("application/json", request.Content!.Headers.ContentType!.MediaType);
            Assert.AreEqual(exists ? "\"v1\"" : "*", (exists ? request.Headers.IfMatch : request.Headers.IfNoneMatch).Single().ToString());
            return new HttpResponseMessage(HttpStatusCode.PreconditionFailed) { Content = content };
        });
        var upload = await store.UploadAsync(Settings(), "secret", new StatisticsDocument(),
            new StatisticsSyncRemoteInfo { Exists = exists, EntityTag = "v1" }, default);
        Assert.IsTrue(upload.HasConflict);
        Assert.IsNull(upload.Result);
        Assert.IsTrue(content.IsDisposed);
    }

    [TestMethod]
    public async Task UploadFallsBackToTimestampAndRequiresReturnedEntityTag()
    {
        var content = new TrackingContent(string.Empty);
        using var store = CreateStore(request =>
        {
            Assert.AreEqual(Now, request.Headers.IfUnmodifiedSince);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => store.UploadAsync(Settings(), "secret", new StatisticsDocument(),
            new StatisticsSyncRemoteInfo { Exists = true, LastModifiedAt = Now }, default));
        Assert.IsTrue(content.IsDisposed);
    }

    [TestMethod]
    public async Task UnversionedExistingObjectIsNeverOverwritten()
    {
        using var store = CreateStore(_ => throw new AssertFailedException("不应发送请求"));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => store.UploadAsync(Settings(), "secret", new StatisticsDocument(),
            new StatisticsSyncRemoteInfo { Exists = true }, default));
    }

    [TestMethod]
    public async Task SuccessfulUploadReturnsVersionWithoutMutatingInput()
    {
        var document = new StatisticsDocument { Info = new StatisticsDocumentInfo { ExportApp = "original" } };
        using var store = CreateStore(_ => new HttpResponseMessage(HttpStatusCode.OK) { Headers = { ETag = new EntityTagHeaderValue("\"uploaded\"") } });
        var upload = await store.UploadAsync(Settings(), "secret", document, new StatisticsSyncRemoteInfo(), default);
        Assert.IsFalse(upload.HasConflict);
        Assert.AreEqual("uploaded", upload.Result!.EntityTag);
        Assert.AreEqual(Now, upload.Result.RemoteLastModifiedAt);
        Assert.AreEqual("original", document.Info.ExportApp);
    }

    private static StatisticsSyncSettings Settings() => new() { Endpoint = "account", BucketName = "bucket", RemotePath = "statistics.json", UserName = "test-key" };
    private static S3StatisticsRemoteStore CreateStore(Func<HttpRequestMessage, HttpResponseMessage> handle) =>
        new(new HttpClient(new Handler(handle)), new FixedTime());
    private sealed class FixedTime : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(handle(request));
    }
    private sealed class TrackingContent(string value) : StringContent(value)
    {
        public bool IsDisposed { get; private set; }
        protected override void Dispose(bool disposing) { IsDisposed = true; base.Dispose(disposing); }
    }
}

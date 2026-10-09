using System.Net;
using Aibysitter.Rules.PullRequests;
using Aibysitter.Web.Content;
using Aibysitter.Web.GitHub;
using Aibysitter.Web.Incidents;
using Aibysitter.Web.Linting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static Aibysitter.Rules.Tests.RawGitHubFetcherTests;

namespace Aibysitter.Rules.Tests.GitHub;

/// <summary>Security review 2026-10-09, low items 16, 17, 19, 20, 21, 22, 23, 24. Also: HardeningTests (24), ActionScriptTests (27), ReleaseWorkflowTests (28).</summary>
public class SecurityLowTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly PullRequestRef Pr = new(42, "o", "r", 7, "abcdef0123");

    // Item 16: a store exception does not stop the worker.

    private sealed class ThrowingStore(ReviewJob bad, bool onComplete) : IReviewJobStore
    {
        public bool IsDurable => false;

        public StoredJobState Find(string? deliveryId, out ReviewJob? job)
        {
            job = null;
            return StoredJobState.None;
        }

        public void Save(ReviewJob job)
        {
        }

        public IReviewJobLease? TryAcquire(ReviewJob job) =>
            job == bad && !onComplete ? throw new UnauthorizedAccessException("denied") : new Lease(job == bad && onComplete);

        public IReadOnlyList<ReviewJob> LoadPending() => [];

        private sealed class Lease(bool throwOnComplete) : IReviewJobLease
        {
            public int Attempts => 1;

            public void Complete()
            {
                if (throwOnComplete)
                {
                    throw new IOException("locked");
                }
            }

            public void Dispose()
            {
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoreException_NextJobStillReviewed(bool onComplete)
    {
        var fake = new FakeGitHubGateway();
        var queue = new ReviewQueue();
        var first = new ReviewJob(Pr, 1, Guid.NewGuid().ToString("D"));
        var second = new ReviewJob(Pr, 2, Guid.NewGuid().ToString("D"));
        var worker = new ReviewWorker(queue, new ReviewProcessor(fake, new PullRequestReviewer(), NullLogger<ReviewProcessor>.Instance),
            new ThrowingStore(first, onComplete), fake, new ImmediateTime(), NullLogger<ReviewWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        queue.TryEnqueue(first);
        queue.TryEnqueue(second);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!fake.Calls.Contains("complete 2") && DateTime.UtcNow < deadline && !worker.ExecuteTask!.IsCompleted)
        {
            await Task.Delay(20);
        }

        Assert.Contains("complete 2", fake.Calls);
        Assert.False(worker.ExecuteTask!.IsCompleted);
        await worker.StopAsync(CancellationToken.None);
    }

    // Item 17: decode and mid-body errors are a failed probe, not an exception.

    private sealed class ThrowingStream(Exception error) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw error;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw error;
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw error;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public static TheoryData<Exception> BodyErrors() => new() { new InvalidDataException("bad gzip"), new IOException("reset") };

    [Theory]
    [MemberData(nameof(BodyErrors))]
    public async Task BodyReadError_Unreachable(Exception error)
    {
        var raw = new FakeRaw();
        raw.Routes[RawGitHubFetcher.RawUrl(RawGitHubFetcherTests.Repo, "CLAUDE.md").ToString()] =
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ThrowingStream(error)) };

        var result = await new RawGitHubFetcher(new HttpClient(raw)).FetchAsync(RawGitHubFetcherTests.Repo, null, CancellationToken.None);

        Assert.Equal(FetchStatus.Unreachable, result.Status);
    }

    // Item 19: the comment fallback cuts at a line end.

    [Fact]
    public void OversizedComment_CutAtLineEnd()
    {
        var line = "- `" + new string('a', 95) + "`";
        var summary = string.Join('\n', Enumerable.Repeat(line, 600));
        var report = new CheckRunReport(ReviewConclusion.Neutral, "No findings", summary, []);

        var body = ReviewComment.Build(report, Pr, 777);
        var kept = body[..body.LastIndexOf("\n\n[View the check run]", StringComparison.Ordinal)].Split('\n');

        Assert.True(body.Length <= ReviewComment.MaxLength);
        Assert.Equal("…", kept[^1]);
        Assert.Equal(line, kept[^2]);
        Assert.All(kept.Skip(3).SkipLast(1), l => Assert.Equal(line, l));
    }

    // Item 20: a review over the time limit closes under the item-6 rules.

    [Theory]
    [InlineData("{}", ReviewConclusion.Neutral)]
    [InlineData("{\"conclusion\": \"fail-on-warnings\"}", ReviewConclusion.Failure)]
    public async Task ReviewOverTimeLimit_ClosedAsFailed(string config, ReviewConclusion expected)
    {
        var fake = new FakeGitHubGateway { HangOnFiles = true };
        fake.Contents[RepoConfig.FilePath] = config;
        using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<TimeProvider>(new ImmediateTime())
            .AddSingleton<IGitHubGateway>(fake)
            .AddSingleton(new PullRequestReviewer())
            .AddSingleton<ReviewProcessor>()
            .BuildServiceProvider();

        var processing = services.GetRequiredService<ReviewProcessor>().ProcessAsync(new ReviewJob(Pr, 777, Guid.NewGuid().ToString("D")), CancellationToken.None);
        var closed = await Task.WhenAny(processing, Task.Delay(TimeSpan.FromSeconds(10))) == processing;

        Assert.True(closed, "review did not stop");
        var report = await fake.Completed.Task;
        Assert.Equal(("Review failed", "Review stopped: it exceeded the 90 second limit.", expected), (report.Title, report.Summary, report.Conclusion));
        Assert.Equal((TimeSpan.FromSeconds(90), CheckRunReport.BudgetSummary), (ReviewProcessor.JobBudget, report.Summary));
    }

    // Item 21: protocol-relative incident sources are rejected.

    [Theory]
    [InlineData("//evil.example/x")]
    [InlineData("/\\evil.example/x")]
    public void IncidentSource_ProtocolRelative_Rejected(string source)
    {
        var body = "## What happened\n- x\n\n## Impact\n- x\n\n## Detection\nx\n\n## Why review and CI missed it\n- x\n\n## Fix\n- x\n";
        var entry = $"---\nid: 7\ntitle: T\ndate: 2026-10-02\nagent: A\nsubmitter: some-user\ncaught-by: none\nsource: {source}\n---\n" + body;

        var error = Assert.Throws<IncidentFormatException>(() => new IncidentCatalog(new Dictionary<string, string> { ["0007-x.md"] = entry }));

        Assert.Equal(["0007-x.md: source must be an https URL or a site path"], error.Errors);
    }

    // Item 22: Markdown links keep only http, https, mailto and relative targets.

    [Fact]
    public void Markdown_UnsafeSchemes_Dropped_TextKept()
    {
        var html = MarkdownRenderer.ToHtml("[a](javascript:alert(1)) ![i](javascript:x) <javascript:alert(1)> [b](JAVASCRIPT:x) [c](data:text/html,x) [d](<java\tscript:x>)");

        Assert.Equal("<p>a i javascript:alert(1) b c d</p>", html.Trim());
    }

    [Fact]
    public void Markdown_SafeLinks_Unchanged()
    {
        var html = MarkdownRenderer.ToHtml("[a](https://example.com/x) [b](/Rules/R001) [c](#top) [d](mailto:a@example.com) ![i](/img/x.png) <https://example.com>");

        Assert.Contains("<a href=\"https://example.com/x\">a</a>", html);
        Assert.Contains("<a href=\"/Rules/R001\">b</a>", html);
        Assert.Contains("<a href=\"#top\">c</a>", html);
        Assert.Contains("<a href=\"mailto:a@example.com\">d</a>", html);
        Assert.Contains("<img src=\"/img/x.png\" alt=\"i\" />", html);
        Assert.Contains("<a href=\"https://example.com\">https://example.com</a>", html);
    }

    // Item 24: rate-limit keys; IPv6 by /64. Bucket sharing is tested in HardeningTests.

    [Theory]
    [InlineData("198.51.100.7", "198.51.100.7")]
    [InlineData("::ffff:198.51.100.7", "198.51.100.7")]
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2::/64")]
    [InlineData("::1", "::/64")]
    public void ClientKey_Ipv4AsIs_Ipv6By64(string address, string key) =>
        Assert.Equal(key, Web.Infrastructure.ClientKey.For(IPAddress.Parse(address)));

    [Fact]
    public void ClientKey_NoAddress_Unknown() => Assert.Equal("unknown", Web.Infrastructure.ClientKey.For(null));

    // Item 23: absolute URLs come from Site:BaseUrl, not the Host header.

    [Fact]
    public async Task AbsoluteUrls_IgnoreHostHeader()
    {
        var client = factory.CreateClient();
        var id = factory.Services.GetRequiredService<Aibysitter.Web.Gallery.GalleryCatalog>().All[0].Id;

        foreach (var path in new[] { "/registry.json", $"/Gallery/{id}" })
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Host = "evil.example";
            var text = await (await client.SendAsync(request)).Content.ReadAsStringAsync();

            Assert.DoesNotContain("evil.example", text, StringComparison.Ordinal);
            Assert.Contains("https://aibysitting.net/", text, StringComparison.Ordinal);
        }
    }
}

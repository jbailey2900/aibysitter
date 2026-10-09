using System.Net;
using System.Text;
using Aibysitter.Rules.Tests.Data;
using Aibysitter.Web.GitHub;
using Aibysitter.Web.Linting;
using Microsoft.AspNetCore.Mvc.Testing;
using static Aibysitter.Rules.Tests.GitHub.WebhookTestData;
using static Aibysitter.Rules.Tests.RawGitHubFetcherTests;

namespace Aibysitter.Rules.Tests.GitHub;

/// <summary>Security review 2026-10-09, Web mediums: items 10 and 13 (item 9 is in HardeningTests).</summary>
public class SecurityMediumWebTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    // Item 10: webhook body cap and failed-signature limit.

    private HttpClient Webhook(int failedSignatureLimit = 60) =>
        GitHubWebhookTests.Create(factory.WithWebHostBuilder(b => b.UseSetting("RateLimiting:Webhook:PermitLimit", failedSignatureLimit.ToString())),
            new FakeGitHubGateway(), runWorker: false).Client;

    /// <summary>A body with no Content-Length, as with chunked transfer.</summary>
    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    [Fact]
    public async Task ChunkedBodyOverCap_413()
    {
        var request = Request("pull_request", "{}");
        request.Content = new UnknownLengthContent(new byte[GitHubWebhookEndpoint.MaxBodyBytes + 1]);

        var response = await Webhook().SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(1024 * 1024, GitHubWebhookEndpoint.MaxBodyBytes);
    }

    [Fact]
    public async Task FailedSignatures_Limited_SignedDeliveriesNotCounted()
    {
        var client = Webhook(failedSignatureLimit: 2);

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Request("ping", "{}"))).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Request("ping", "{}", signature: "sha256=00"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Request("ping", "{}", signature: "sha256=00"))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(Request("ping", "{}", signature: "sha256=00"))).StatusCode);
    }

    [Fact]
    public void FailedSignatureLimit_DefaultsTo60PerMinute()
    {
        var settings = new Web.Infrastructure.WebhookRateLimitSettings();

        Assert.Equal((60, 60), (settings.PermitLimit, settings.WindowSeconds));
    }

    // Item 13: badge fetch cost.

    [Fact]
    public async Task Redirects_OneBudgetPerFetch()
    {
        var raw = new FakeRaw();
        foreach (var name in RawGitHubFetcher.FileNames)
        {
            var url = RawGitHubFetcher.RawUrl(RawGitHubFetcherTests.Repo, name).ToString();
            for (var hop = 0; hop < 4; hop++)
            {
                var from = hop == 0 ? url : $"{url}/{hop}";
                var to = $"{url}/{hop + 1}";
                raw.Routes[from] = () =>
                {
                    var response = new HttpResponseMessage(HttpStatusCode.MovedPermanently);
                    response.Headers.Location = new Uri(to);
                    return response;
                };
            }
        }

        await new RawGitHubFetcher(new HttpClient(raw)).FetchAsync(RawGitHubFetcherTests.Repo, null, CancellationToken.None);

        Assert.Equal(RawGitHubFetcher.FileNames.Count + RawGitHubFetcher.MaxRedirects, raw.Requested.Count);
    }

    [Fact]
    public async Task ConcurrentMissesOnOneKey_OneFetch()
    {
        var raw = new FakeRaw { Delay = TimeSpan.FromMilliseconds(300) };
        raw.File("CLAUDE.md", "# Rules\n- Use tabs.\n");
        var history = new FakeScoreHistory();
        var client = ScoreHistoryPageTests.Client(factory, raw, history);

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => client.GetAsync("/badge/o/r.svg")));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(RawGitHubFetcher.FileNames.Count, raw.Requested.Count);
        Assert.Single(history.Records);
    }

    [Fact]
    public async Task MissesOverCap_UnknownNoStore_NotCached()
    {
        var raw = new FakeRaw { Delay = TimeSpan.FromMilliseconds(1_500) };
        var client = ScoreHistoryPageTests.Client(factory, raw, new FakeScoreHistory());

        var responses = await Task.WhenAll(Enumerable.Range(0, BadgeService.MaxConcurrentMisses + 1).Select(i => client.GetAsync($"/badge/o/r{i}.svg")));
        var busy = responses.Where(r => r.Headers.CacheControl?.NoStore == true).ToList();

        Assert.Single(busy);
        Assert.Contains(">unknown</text>", await busy[0].Content.ReadAsStringAsync());
        Assert.Equal(8, BadgeService.MaxConcurrentMisses);
    }
}

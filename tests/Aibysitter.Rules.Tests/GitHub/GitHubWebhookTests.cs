using System.Net;
using Aibysitter.Web.GitHub;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using static Aibysitter.Rules.Tests.GitHub.WebhookTestData;
using static Aibysitter.Rules.Tests.PullRequests.PrTestData;

namespace Aibysitter.Rules.Tests.GitHub;

public class GitHubWebhookTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    internal static (HttpClient Client, IServiceProvider Services) Create(
        WebApplicationFactory<Program> factory,
        FakeGitHubGateway fake,
        bool runWorker,
        string appId = "1",
        string? queuePath = null,
        Action<IServiceCollection>? configure = null)
    {
        var app = factory.WithWebHostBuilder(b => b
            .UseSetting("GitHub:AppId", appId)
            .UseSetting("ReviewQueue:Path", queuePath ?? string.Empty)
            .UseSetting("GitHub:WebhookSecret", Secret)
            .UseSetting("GitHub:PrivateKeyPath", "unused.pem")
            .ConfigureServices(s =>
            {
                s.RemoveAll<IGitHubGateway>();
                s.AddSingleton<IGitHubGateway>(fake);
                if (!runWorker)
                {
                    foreach (var worker in s.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(ReviewWorker)).ToList())
                    {
                        s.Remove(worker);
                    }
                }

                configure?.Invoke(s);
            }));

        return (app.CreateClient(), app.Services);
    }

    private (HttpClient Client, IServiceProvider Services) Create(FakeGitHubGateway fake, bool runWorker, string appId = "1") =>
        Create(factory, fake, runWorker, appId);

    internal static async Task<ReviewJob?> TryDequeue(IServiceProvider services)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        try
        {
            await foreach (var job in services.GetRequiredService<ReviewQueue>().ReadAllAsync(cts.Token))
            {
                return job;
            }
        }
        catch (OperationCanceledException)
        {
        }

        return null;
    }

    [Fact]
    public async Task NotConfigured_Returns503()
    {
        var response = await factory.CreateClient().SendAsync(Request("ping", "{}"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task BadSignature_Returns401_NoGitHubCalls()
    {
        var fake = new FakeGitHubGateway();
        var (client, _) = Create(fake, runWorker: false);

        var response = await client.SendAsync(Request("pull_request", PullRequestPayload(), signature: "sha256=" + new string('0', 64)));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(fake.Calls);
    }

    [Theory]
    [InlineData("5152568")]
    [InlineData("Iv23liExampleClient")]
    public async Task Ping_WithAppIdOrClientId_Returns200(string appId)
    {
        var (client, _) = Create(new FakeGitHubGateway(), runWorker: false, appId);

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Request("ping", "{}"))).StatusCode);
    }

    [Fact]
    public async Task Ping_Returns200()
    {
        var (client, _) = Create(new FakeGitHubGateway(), runWorker: false);

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Request("ping", "{\"zen\":\"x\"}"))).StatusCode);
    }

    [Theory]
    [InlineData("push", "opened")]
    [InlineData("pull_request", "closed")]
    [InlineData("pull_request", "labeled")]
    public async Task IgnoredEventsAndActions_Return204_NoGitHubCalls(string eventName, string action)
    {
        var fake = new FakeGitHubGateway();
        var (client, _) = Create(fake, runWorker: false);

        var response = await client.SendAsync(Request(eventName, PullRequestPayload(action)));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task MalformedPullRequestPayload_Returns400()
    {
        var (client, _) = Create(new FakeGitHubGateway(), runWorker: false);

        var response = await client.SendAsync(Request("pull_request", "{\"action\":\"opened\"}"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("opened")]
    [InlineData("synchronize")]
    [InlineData("reopened")]
    public async Task ReviewedAction_CreatesQueuedCheckRun_ThenEnqueues_Returns202(string action)
    {
        var fake = new FakeGitHubGateway();
        var (client, services) = Create(fake, runWorker: false);

        var response = await client.SendAsync(Request("pull_request", PullRequestPayload(action)));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(new[] { "create jbailey2900/sandbox#7@0123456" }, fake.Calls);
        var job = await TryDequeue(services);
        Assert.NotNull(job);
        Assert.Equal(777, job.CheckRunId);
        Assert.Equal(new PullRequestRef(42, "jbailey2900", "sandbox", 7, "0123456789abcdef0123456789abcdef01234567", WebhookTestData.BaseSha), job.PullRequest);
    }

    [Fact]
    public async Task CheckRunCreationFails_Returns502_NothingEnqueued()
    {
        var fake = new FakeGitHubGateway { ThrowOnCreate = new HttpRequestException("boom") };
        var (client, services) = Create(fake, runWorker: false);

        var response = await client.SendAsync(Request("pull_request", PullRequestPayload()));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Null(await TryDequeue(services));
    }

    [Fact]
    public async Task EndToEnd_WorkerReviewsAndCompletesCheckRun()
    {
        var fake = new FakeGitHubGateway();
        fake.Files.Add(Added("src/OrderService.cs", "var key = YOUR_API_KEY;"));
        fake.BaseContents[".github/aibysitter.json"] = "{\"conclusion\": \"fail-on-errors\"}";
        var (client, _) = Create(fake, runWorker: true);

        var response = await client.SendAsync(Request("pull_request", PullRequestPayload()));
        var report = await fake.Completed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(Aibysitter.Rules.PullRequests.ReviewConclusion.Failure, report.Conclusion);
        Assert.Equal("P001 Placeholder identifiers", Assert.Single(report.Annotations).Title);
        Assert.Equal(
            new[] { "create jbailey2900/sandbox#7@0123456", "in_progress 777", "files", "base content .github/aibysitter.json", "content src/OrderService.cs", "complete 777" },
            fake.Calls);
    }
}

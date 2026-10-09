using System.Net;
using System.Text.RegularExpressions;
using Aibysitter.Rules.PullRequests;
using Aibysitter.Rules.Tests.Data;
using Aibysitter.Rules.Tests.GitHub;
using Aibysitter.Web.GitHub;
using Aibysitter.Web.Linting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static Aibysitter.Rules.Tests.PullRequests.PrTestData;
using static Aibysitter.Rules.Tests.RawGitHubFetcherTests;

namespace Aibysitter.Rules.Tests;

/// <summary>A pattern that hits the match timeout gives a clear error at every entry point, never a 500.</summary>
public class RegexTimeoutTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Trigger = "TRIGGER-REGEX-TIMEOUT";

    /// <summary>Throws the timeout when the file contains <see cref="Trigger"/>.</summary>
    private sealed class TimeoutRule : IRule
    {
        public string Id => "R099";

        public string Title => "Timeout";

        public Severity Severity => Severity.Info;

        public IEnumerable<Finding> Evaluate(RulesFile file) =>
            file.Lines.Any(l => l.Text.Contains(Trigger, StringComparison.Ordinal))
                ? throw new RegexMatchTimeoutException("x", "y", RegexTimeout.Default)
                : [];
    }

    private sealed class TimeoutCheck : IPullRequestCheck
    {
        public string Id => "P099";

        public string Title => "Timeout";

        public Severity Severity => Severity.Info;

        public IEnumerable<PullRequestFinding> Evaluate(PullRequestContext context) =>
            throw new RegexMatchTimeoutException("x", "y", RegexTimeout.Default);
    }

    private HttpClient Client(FakeRaw? raw = null, FakeScoreHistory? history = null) =>
        factory.WithWebHostBuilder(b => b
            .UseSetting("RateLimiting:Lint:PermitLimit", "100000")
            .ConfigureServices(s => s.AddHttpClient<RawGitHubFetcher>().ConfigurePrimaryHttpMessageHandler(() => raw ?? new FakeRaw()))
            .ConfigureTestServices(s =>
            {
                s.AddSingleton(new LintEngine(new LintEngine().Rules.Append(new TimeoutRule())));
                s.AddSingleton<Aibysitter.Web.Data.IScoreHistory>(history ?? new FakeScoreHistory());
            }))
            .CreateClient();

    [Fact]
    public void Default_IsOneSecond_AndAppliedByTheWebHost()
    {
        _ = factory.Server;

        Assert.Equal(TimeSpan.FromSeconds(1), RegexTimeout.Default);
        Assert.Equal(RegexTimeout.Default, AppContext.GetData(RegexTimeout.AppContextKey));
    }

    [Fact]
    public async Task Form_ShowsMessage_200()
    {
        var response = await LintClient.PostAsync(Client(), Trigger);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(RegexTimeout.LintMessage, html);
        Assert.DoesNotContain("<h2>Findings", html);
    }

    [Fact]
    public async Task LintByUrl_ShowsMessage_NoHistory()
    {
        var raw = new FakeRaw();
        raw.File("CLAUDE.md", "# Rules\n" + Trigger);
        var history = new FakeScoreHistory();

        var response = await LintClient.PostUrlAsync(Client(raw, history), "o/r");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"<p class=\"error\" role=\"alert\">{RegexTimeout.LintMessage}</p>", html);
        Assert.Empty(history.Records);
    }

    [Fact]
    public async Task Api_422Problem_NoStore()
    {
        var response = await Client().PostAsync(LintApi.Path, new StringContent($"{{\"content\":\"{Trigger}\"}}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains(RegexTimeout.LintMessage, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Badge_Unknown_NoHistory()
    {
        var raw = new FakeRaw();
        raw.File("CLAUDE.md", "# Rules\n" + Trigger);
        var history = new FakeScoreHistory();

        var response = await Client(raw, history).GetAsync("/badge/o/r.svg");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(">unknown</text>", await response.Content.ReadAsStringAsync());
        Assert.Empty(history.Records);
    }

    [Fact]
    public async Task Review_ClosesWithTimeoutSummary()
    {
        var fake = new FakeGitHubGateway();
        fake.Files.Add(Added("src/A.cs", "var a = 1;"));

        await new ReviewProcessor(fake, new PullRequestReviewer([new TimeoutCheck()]), NullLogger<ReviewProcessor>.Instance)
            .ProcessAsync(new ReviewJob(new PullRequestRef(1, "o", "r", 7, "abcdef0"), 99, "d"), CancellationToken.None);
        var report = await fake.Completed.Task;

        Assert.Equal(CheckRunReport.TimeoutSummary, report.Summary);
        Assert.Equal(ReviewConclusion.Neutral, report.Conclusion);
    }

    [Fact]
    public async Task ConfigPage_PathologicalGlob_Fast200()
    {
        var client = Client();
        var form = await client.GetStringAsync("/GitHub/Config");
        var token = System.Text.RegularExpressions.Regex.Match(form, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Posted"] = "true",
            ["Scope"] = "*a*a*a*a*a*a*b",
            ["TestPath"] = new string('a', 1_000),
            ["Conclusion"] = "Advisory",
        });

        HttpResponseMessage? response = null;
        Timing.AssertFast("config page", () => response = client.PostAsync("/GitHub/Config", content).GetAwaiter().GetResult());

        Assert.Equal(HttpStatusCode.OK, response!.StatusCode);
        Assert.Contains("is outside scope", await response.Content.ReadAsStringAsync());
    }
}

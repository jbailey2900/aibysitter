using System.Net;
using System.Text.Json;
using Aibysitter.Rules.PullRequests;
using Aibysitter.Web.GitHub;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using static Aibysitter.Rules.Tests.GitHub.WebhookTestData;

namespace Aibysitter.Rules.Tests.GitHub;

/// <summary>P008 justifications in the App: description and workflow fetches, summary notes, edited events.</summary>
public class DeletedTestJustificationAppTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Removed = "tests/Orders/OrderServiceTests.cs";
    private static readonly ReviewJob Job = new(new PullRequestRef(42, "o", "r", 7, "abcdef0123"), 99, "delivery-1");

    private static ReviewProcessor Processor(FakeGitHubGateway fake) => new(fake, new PullRequestReviewer(), NullLogger<ReviewProcessor>.Instance);

    private static FakeGitHubGateway WithRemovedTest(string description)
    {
        var fake = new FakeGitHubGateway { Description = description, Paths = ["src/Orders/OrderService.cs", "tools/smoke.ps1", ".github/workflows/ci.yml", ".github/workflows/docs.yaml"] };
        fake.Files.Add(new ChangedFile(Removed, FileChangeStatus.Removed, "@@ -1 +0,0 @@\n-x"));
        fake.Contents[".github/workflows/ci.yml"] = "steps:\n  - run: pwsh tools/smoke.ps1\n";
        fake.Contents[".github/workflows/docs.yaml"] = "steps: []\n";
        fake.Contents[RepoConfig.FilePath] = "{\"conclusion\": \"fail-on-errors\"}";
        return fake;
    }

    [Fact]
    public async Task WorkflowJustification_InfoFinding_ReadsDescriptionAndWorkflows()
    {
        var fake = WithRemovedTest($"Aibysitter: P008 {Removed} -> tools/smoke.ps1");

        await Processor(fake).ProcessAsync(Job, CancellationToken.None);

        var report = await fake.Completed.Task;
        Assert.Equal(ReviewConclusion.Neutral, report.Conclusion);
        Assert.Equal("1 finding (0 errors, 0 warnings, 1 notice)", report.Title);
        Assert.Contains($"Test file deleted: {Removed}. Replaced by tools/smoke.ps1 (referenced by .github/workflows/ci.yml).", Unescape(report.Summary));
        Assert.Contains("description", fake.Calls);
        Assert.Contains("content .github/workflows/ci.yml", fake.Calls);
        Assert.Contains("content .github/workflows/docs.yaml", fake.Calls);
    }

    [Fact]
    public async Task TestFileJustification_NoWorkflowReads()
    {
        var fake = WithRemovedTest($"Aibysitter: P008 {Removed} -> tests/Orders/OrderTests.cs");
        fake.Paths!.Add("tests/Orders/OrderTests.cs");

        await Processor(fake).ProcessAsync(Job, CancellationToken.None);

        Assert.Equal(ReviewConclusion.Neutral, (await fake.Completed.Task).Conclusion);
        Assert.DoesNotContain(fake.Calls, c => c.StartsWith("content .github/workflows/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvalidJustification_NoteInSummary()
    {
        var fake = WithRemovedTest($"Aibysitter: P008 {Removed} -> src/Orders/OrderService.cs");

        await Processor(fake).ProcessAsync(Job, CancellationToken.None);

        var report = await fake.Completed.Task;
        Assert.Equal(ReviewConclusion.Failure, report.Conclusion);
        Assert.Contains($"P008 justification for {Removed} not applied: src/Orders/OrderService.cs is not a test file and no workflow under .github/workflows/ references it.", Unescape(report.Summary));
    }

    /// <summary>The summary escapes Markdown punctuation with backslashes.</summary>
    private static string Unescape(string summary) => summary.Replace("\\", "", StringComparison.Ordinal);

    [Fact]
    public async Task NoRemovedTest_DescriptionNotRead()
    {
        var fake = new FakeGitHubGateway { Description = $"Aibysitter: P008 {Removed} -> tools/smoke.ps1" };
        fake.Files.Add(PullRequests.PrTestData.Added("src/A.cs", "var a = 1;"));

        await Processor(fake).ProcessAsync(Job, CancellationToken.None);
        await fake.Completed.Task;

        Assert.DoesNotContain("description", fake.Calls);
    }

    private const string Line = $"Aibysitter: P008 {Removed} -> tools/smoke.ps1";

    private static string EditedPayload(object changes, string? body = null, string state = "open") =>
        JsonSerializer.Serialize(new
        {
            action = "edited",
            changes,
            installation = new { id = 42 },
            repository = new { name = "sandbox", owner = new { login = "jbailey2900" } },
            pull_request = new { number = 7, state, body, head = new { sha = "0123456789abcdef0123456789abcdef01234567" }, @base = new { sha = BaseSha } },
        });

    private async Task<HttpStatusCode> SendEdited(string payload)
    {
        var (client, _) = GitHubWebhookTests.Create(factory, new FakeGitHubGateway(), runWorker: false);
        return (await client.SendAsync(Request("pull_request", payload))).StatusCode;
    }

    public static TheoryData<string?, string?> JustificationChanges() => new()
    {
        { "Moves the tests.", $"Moves the tests.\n\n{Line}" },
        { $"Moves the tests.\r\n{Line}", "Moves the tests." },
        { Line, $"Aibysitter: P008 {Removed} -> tests/Orders/OrderTests.cs" },
        { null, Line },
    };

    [Theory]
    [MemberData(nameof(JustificationChanges))]
    public async Task EditedAibysitterLines_Reviewed(string? from, string? to) =>
        Assert.Equal(HttpStatusCode.Accepted, await SendEdited(EditedPayload(new { body = new { from } }, to)));

    [Theory]
    [InlineData("Moves the tests.", "Moves the order tests.")]
    [InlineData($"Old text.\n{Line}", $"New text.\r\n  {Line}  ")]
    [InlineData(null, "Moves the tests.")]
    [InlineData("Moves the tests.", null)]
    public async Task EditedOtherText_NotReviewed(string? from, string? to) =>
        Assert.Equal(HttpStatusCode.NoContent, await SendEdited(EditedPayload(new { body = new { from } }, to)));

    [Fact]
    public async Task EditedOnClosedPullRequest_NotReviewed() =>
        Assert.Equal(HttpStatusCode.NoContent, await SendEdited(EditedPayload(new { body = new { from = "Moves the tests." } }, Line, state: "closed")));

    [Fact]
    public async Task EditedTitleOnly_NotReviewed() =>
        Assert.Equal(HttpStatusCode.NoContent, await SendEdited(EditedPayload(new { title = new { from = "old" } }, Line)));
}

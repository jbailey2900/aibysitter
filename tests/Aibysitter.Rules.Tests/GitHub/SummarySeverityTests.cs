using Aibysitter.Rules.PullRequests;
using Aibysitter.Web.GitHub;
using Microsoft.Extensions.Logging.Abstractions;
using static Aibysitter.Rules.Tests.PullRequests.PrTestData;

namespace Aibysitter.Rules.Tests.GitHub;

/// <summary>The summary table counts each finding under its effective severity: a justified finding under Info.</summary>
public class SummarySeverityTests
{
    private const string Removed = "tests/Orders/OrderServiceTests.cs";
    private static readonly ReviewJob Job = new(new PullRequestRef(42, "o", "r", 7, "abcdef0123", "base0123456"), 99, "delivery-1");

    private static async Task<CheckRunReport> Review(Action<FakeGitHubGateway> setup)
    {
        var fake = new FakeGitHubGateway();
        fake.BaseContents[RepoConfig.FilePath] = """{ "conclusion": "fail-on-warnings" }""";
        setup(fake);
        await new ReviewProcessor(fake, new PullRequestReviewer(), NullLogger<ReviewProcessor>.Instance).ProcessAsync(Job, CancellationToken.None);
        return await fake.Completed.Task;
    }

    private static List<string> Rows(CheckRunReport report, string check) =>
        report.Summary.Split('\n').Where(l => l.StartsWith($"| {check} ", StringComparison.Ordinal)).Select(l => l.TrimEnd('\r')).ToList();

    [Fact]
    public async Task JustifiedAndUnjustifiedP009_OneRowPerSeverity()
    {
        var report = await Review(f => f.Files.Add(Added("src/Cleanup.cs",
            "catch (IOException) { } // aibysitter-ignore P009: best-effort temp cleanup",
            "",
            "catch (Exception) { }")));

        Assert.Equal(["| P009 Swallowed exceptions | Warning | 1 |", "| P009 Swallowed exceptions | Info | 1 |"], Rows(report, "P009"));
        Assert.Equal("2 findings (0 errors, 1 warning, 1 notice)", report.Title);
        Assert.Equal(ReviewConclusion.Failure, report.Conclusion);
    }

    [Fact]
    public async Task OnlyJustifiedP009_InfoRowOnly()
    {
        var report = await Review(f => f.Files.Add(Added("src/Cleanup.cs", "catch (IOException) { } // aibysitter-ignore P009: best-effort temp cleanup")));

        Assert.Equal(["| P009 Swallowed exceptions | Info | 1 |"], Rows(report, "P009"));
        Assert.Equal(ReviewConclusion.Neutral, report.Conclusion);
    }

    [Fact]
    public async Task JustifiedP008_InfoRow()
    {
        var report = await Review(f =>
        {
            f.Description = $"Aibysitter: P008 {Removed} -> tests/Orders/OrderTests.cs";
            f.Paths = ["src/Orders/OrderService.cs", "tests/Orders/OrderTests.cs"];
            f.Files.Add(new ChangedFile(Removed, FileChangeStatus.Removed, "@@ -1 +0,0 @@\n-x"));
        });

        Assert.Equal(["| P008 Deleted tests | Info | 1 |"], Rows(report, "P008"));
    }

    [Fact]
    public async Task NoFindings_DeclaredSeverityRow() =>
        Assert.Equal(["| P009 Swallowed exceptions | Warning | 0 |"], Rows(await Review(f => f.Files.Add(Added("src/A.cs", "var a = 1;"))), "P009"));
}

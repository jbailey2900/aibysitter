using Aibysitter.Rules.PullRequests;
using Aibysitter.Web.GitHub;
using Microsoft.Extensions.Logging.Abstractions;
using static Aibysitter.Rules.Tests.PullRequests.PrTestData;

namespace Aibysitter.Rules.Tests.GitHub;

/// <summary>P009 justifications in the App: notice annotation with the reason, rejection note in the summary.</summary>
public class SwallowedExceptionJustificationAppTests
{
    private static readonly ReviewJob Job = new(new PullRequestRef(42, "o", "r", 7, "abcdef0123", "base0123456"), 99, "delivery-1");

    private static async Task<CheckRunReport> Review(params string[] lines)
    {
        var fake = new FakeGitHubGateway();
        fake.BaseContents[RepoConfig.FilePath] = """{ "conclusion": "fail-on-warnings" }""";
        fake.Files.Add(Added("src/Cleanup.cs", lines));
        await new ReviewProcessor(fake, new PullRequestReviewer(), NullLogger<ReviewProcessor>.Instance).ProcessAsync(Job, CancellationToken.None);
        return await fake.Completed.Task;
    }

    [Fact]
    public async Task Justified_NoticeWithReason_Neutral()
    {
        var report = await Review("// aibysitter-ignore P009: best-effort temp cleanup", "catch (IOException) { }");

        var annotation = Assert.Single(report.Annotations);
        Assert.Equal(Severity.Info, annotation.Severity);
        Assert.Equal("Exception swallowed: empty catch block. Justified: best-effort temp cleanup", annotation.Message);
        Assert.Equal(ReviewConclusion.Neutral, report.Conclusion);
    }

    [Fact]
    public async Task Rejected_NoteInSummary_Failure()
    {
        var report = await Review("// aibysitter-ignore P009:", "catch (IOException) { }");

        Assert.Equal(ReviewConclusion.Failure, report.Conclusion);
        Assert.Contains("P009 justification at src/Cleanup.cs:2 not applied: no reason.", report.Summary.Replace("\\", "", StringComparison.Ordinal));
    }
}

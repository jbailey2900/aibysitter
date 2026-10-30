using Aibysitter.Rules.PullRequests;
using Aibysitter.Web.GitHub;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aibysitter.Rules.Tests.GitHub;

/// <summary>dependencies.approved is read from the base commit: an entry added in the same pull request does not apply to it.</summary>
public class ApprovedDependencyAppTests
{
    private static readonly ReviewJob Job = new(new PullRequestRef(42, "o", "r", 7, "abcdef0123", "base0123456"), 99, "delivery-1");

    private const string Approved = """{ "conclusion": "fail-on-warnings", "dependencies": { "approved": [ { "name": "Polly", "version": "8.4.0", "reason": "retries for the payment client" } ] } }""";

    private static async Task<CheckRunReport> Review(string? baseConfig, string? headConfig)
    {
        var fake = new FakeGitHubGateway();
        if (baseConfig is not null)
        {
            fake.BaseContents[RepoConfig.FilePath] = baseConfig;
        }

        if (headConfig is not null)
        {
            fake.Contents[RepoConfig.FilePath] = headConfig;
            fake.Files.Add(new ChangedFile(RepoConfig.FilePath, FileChangeStatus.Modified, "@@ -1,1 +1,1 @@\n-{}\n+" + headConfig));
        }

        fake.Files.Add(new ChangedFile("src/Web/Web.csproj", FileChangeStatus.Modified, "@@ -12,1 +12,2 @@\n <ItemGroup>\n+    <PackageReference Include=\"Polly\" Version=\"8.4.0\" />\n"));
        await new ReviewProcessor(fake, new PullRequestReviewer(), NullLogger<ReviewProcessor>.Instance).ProcessAsync(Job, CancellationToken.None);
        return await fake.Completed.Task;
    }

    [Fact]
    public async Task EntryAtBase_Notice_Neutral()
    {
        var report = await Review(Approved, null);

        var annotation = Assert.Single(report.Annotations);
        Assert.Equal(Severity.Info, annotation.Severity);
        Assert.Equal("Approved dependency: Polly (retries for the payment client)", annotation.Message);
        Assert.Equal(ReviewConclusion.Neutral, report.Conclusion);
    }

    [Fact]
    public async Task EntryAddedInSamePullRequest_NotApplied()
    {
        var report = await Review("""{ "conclusion": "fail-on-warnings" }""", Approved);

        Assert.Contains(report.Annotations, a => a.Title.StartsWith("P010", StringComparison.Ordinal) && a.Severity == Severity.Warning);
        Assert.Equal(ReviewConclusion.Failure, report.Conclusion);
    }

    [Fact]
    public async Task EntryWithoutReason_ConfigErrorInSummary_Failure()
    {
        var report = await Review("""{ "conclusion": "fail-on-warnings", "dependencies": { "approved": [ { "name": "Polly", "version": "8.4.0" } ] } }""", null);

        Assert.Contains("\"reason\" is required", report.Summary.Replace("\\", "", StringComparison.Ordinal));
        Assert.Equal(ReviewConclusion.Failure, report.Conclusion);
    }
}

using Aibysitter.Rules.PullRequests;
using Aibysitter.Web.GitHub;
using Microsoft.Extensions.Logging.Abstractions;
using static Aibysitter.Rules.Tests.PullRequests.PrTestData;

namespace Aibysitter.Rules.Tests.GitHub;

public class ReviewProcessorTests
{
    private static readonly ReviewJob Job = new(new PullRequestRef(42, "o", "r", 7, "abcdef0123"), 99, "delivery-1");

    private static ReviewProcessor Processor(FakeGitHubGateway fake) =>
        new(fake, new PullRequestReviewer(), NullLogger<ReviewProcessor>.Instance);

    [Fact]
    public async Task InProgressFails_ReviewStillCompletes()
    {
        var fake = new FakeGitHubGateway { ThrowOnInProgress = new HttpRequestException("404") };
        fake.Files.Add(Added("src/A.cs", "var k = ::KEY::;"));

        Assert.True(await Processor(fake).ProcessAsync(Job, CancellationToken.None));

        var report = await fake.Completed.Task;
        Assert.Equal("1 finding (1 error, 0 warnings)", report.Title);
    }

    [Fact]
    public async Task CompleteAndErrorCloseFail_ReturnsFalse()
    {
        var fake = new FakeGitHubGateway { ThrowOnComplete = new HttpRequestException("404") };

        Assert.False(await Processor(fake).ProcessAsync(Job, CancellationToken.None));
        Assert.Equal(2, fake.Calls.Count(c => c == "complete 99"));
    }

    [Fact]
    public async Task Findings_CompleteNeutral_UnderDefaultConfig()
    {
        var fake = new FakeGitHubGateway();
        fake.Files.Add(Added("src/A.cs", "var k = ::KEY::;"));

        await Processor(fake).ProcessAsync(Job, CancellationToken.None);
        var report = await fake.Completed.Task;

        Assert.Equal(ReviewConclusion.Neutral, report.Conclusion);
        Assert.Equal("1 finding (1 error, 0 warnings)", report.Title);
        Assert.Equal(new[] { "in_progress 99", "files", "content .github/aibysitter.json", "content src/A.cs", "complete 99" }, fake.Calls);
    }

    [Fact]
    public async Task ConfigErrors_AppearInSummary()
    {
        var fake = new FakeGitHubGateway();
        fake.Files.Add(Added("src/A.cs", "public class A { }"));
        fake.Contents[RepoConfig.FilePath] = "{ not json";

        await Processor(fake).ProcessAsync(Job, CancellationToken.None);

        Assert.Contains("invalid JSON", (await fake.Completed.Task).Summary);
    }

    [Theory]
    [InlineData("tests/ATests.cs", FileChangeStatus.Modified, "@@ -1,1 +1,1 @@\n+x", false)]
    [InlineData("tests/ATests.cs", FileChangeStatus.Modified, "@@ -1,1 +1,1 @@\n+[Fact]", true)]
    [InlineData("src/A.cs", FileChangeStatus.Modified, "@@ -1,1 +1,1 @@\n+[Fact]", true)]
    [InlineData("src/A.cs", FileChangeStatus.Modified, "@@ -1,1 +1,1 @@\n+var x = 1;", false)]
    [InlineData("tests/ATests.cs", FileChangeStatus.Removed, "@@ -1,1 +0,0 @@\n-x", false)]
    [InlineData("tests/a_test.py", FileChangeStatus.Modified, "@@ -1,1 +1,1 @@\n+[x]", false)]
    [InlineData("CLAUDE.md", FileChangeStatus.Modified, "@@ -1,1 +1,1 @@\n+x", true)]
    [InlineData("pkg/AGENTS.md", FileChangeStatus.Added, "@@ -0,0 +1,1 @@\n+x", true)]
    [InlineData(".cursor/rules/a.mdc", FileChangeStatus.Renamed, null, true)]
    [InlineData(".github/copilot-instructions.md", FileChangeStatus.Modified, "@@ -1,1 +1,1 @@\n+x", true)]
    [InlineData("docs/copilot-instructions.md", FileChangeStatus.Modified, "@@ -1,1 +1,1 @@\n+x", false)]
    [InlineData("docs/a.mdc", FileChangeStatus.Added, "@@ -0,0 +1,1 @@\n+x", false)]
    [InlineData("CLAUDE.md", FileChangeStatus.Removed, "@@ -1,1 +0,0 @@\n-x", false)]
    public void NeedsHeadContent(string path, FileChangeStatus status, string? patch, bool expected)
    {
        Assert.Equal(expected, ReviewProcessor.NeedsHeadContent(new ChangedFile(path, status, patch)));
    }

    [Fact]
    public async Task RemovedTestFile_ReadsHeadTree_ForP008PackageExemption()
    {
        var fake = new FakeGitHubGateway { Paths = ["sdk/other/src/index.ts"] };
        fake.Files.Add(new ChangedFile("sdk/pkg/test/a.spec.ts", FileChangeStatus.Removed, "@@ -1,1 +0,0 @@\n-x"));
        fake.Files.Add(new ChangedFile("sdk/pkg/src/b.ts", FileChangeStatus.Removed, "@@ -1,1 +0,0 @@\n-x"));

        await Processor(fake).ProcessAsync(Job, CancellationToken.None);
        var report = await fake.Completed.Task;

        Assert.Contains("tree", fake.Calls);
        Assert.DoesNotContain(report.Annotations, a => a.Title.StartsWith("P008", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("tests/a.spec.ts", FileChangeStatus.Removed, "{}", true)]
    [InlineData("tests/a.spec.ts", FileChangeStatus.Modified, "{}", false)]
    [InlineData("src/a.ts", FileChangeStatus.Removed, "{}", false)]
    [InlineData("tests/a.spec.ts", FileChangeStatus.Removed, "{ \"disable\": [\"P008\"] }", false)]
    public void NeedsTreeForDeletedTests_OnlyWhenATestFileIsRemoved(string path, FileChangeStatus status, string config, bool expected) =>
        Assert.Equal(expected, ReviewProcessor.NeedsTreeForDeletedTests([new ChangedFile(path, status)], RepoConfig.Parse(config).Config));

    [Fact]
    public async Task ContentFetches_AreCapped()
    {
        var fake = new FakeGitHubGateway();
        for (var i = 0; i < ReviewProcessor.MaxContentFetches + 20; i++)
        {
            fake.Files.Add(new ChangedFile($"tests/T{i}Tests.cs", FileChangeStatus.Modified, "@@ -1,1 +1,1 @@\n+[Fact]"));
        }

        await Processor(fake).ProcessAsync(Job, CancellationToken.None);

        Assert.Equal(ReviewProcessor.MaxContentFetches, fake.Calls.Count(c => c.StartsWith("content tests/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task GitHubFailure_CompletesCheckRunAsReviewFailed()
    {
        var fake = new FakeGitHubGateway { ThrowOnFiles = new HttpRequestException("rate limited") };

        await Processor(fake).ProcessAsync(Job, CancellationToken.None);
        var report = await fake.Completed.Task;

        Assert.Equal("Review failed", report.Title);
        Assert.Equal(ReviewConclusion.Neutral, report.Conclusion);
        Assert.Contains("HttpRequestException", report.Summary);
    }

    [Fact]
    public async Task FailureWhileClosingCheckRun_DoesNotThrow()
    {
        var fake = new FakeGitHubGateway
        {
            ThrowOnFiles = new HttpRequestException("down"),
            ThrowOnComplete = new HttpRequestException("still down"),
        };

        await Processor(fake).ProcessAsync(Job, CancellationToken.None);

        Assert.Equal("complete 99", fake.Calls.Last());
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        var fake = new FakeGitHubGateway();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        fake.ThrowOnFiles = new OperationCanceledException(cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Processor(fake).ProcessAsync(Job, cts.Token));
    }

    [Fact]
    public async Task RulesFiles_FetchedFirst_UnderTheCap()
    {
        var fake = new FakeGitHubGateway();
        for (var i = 0; i < ReviewProcessor.MaxContentFetches; i++)
        {
            fake.Files.Add(new ChangedFile($"tests/T{i}Tests.cs", FileChangeStatus.Modified, "@@ -1,1 +1,1 @@\n+[Fact]"));
        }

        fake.Files.Add(new ChangedFile("CLAUDE.md", FileChangeStatus.Added, "@@ -0,0 +1,1 @@\n+- Handle errors properly."));
        fake.Contents["CLAUDE.md"] = "- Handle errors properly.";

        await Processor(fake).ProcessAsync(Job, CancellationToken.None);
        var report = await fake.Completed.Task;

        Assert.Contains("content CLAUDE.md", fake.Calls);
        Assert.Equal(ReviewProcessor.MaxContentFetches, fake.Calls.Count(c => c.StartsWith("content ", StringComparison.Ordinal) && c != $"content {Aibysitter.Rules.PullRequests.RepoConfig.FilePath}"));
        Assert.Contains(report.Annotations, a => a.Path == "CLAUDE.md" && a.Title == "P014 Rules file lint");
    }
}

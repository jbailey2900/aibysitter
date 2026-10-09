using Aibysitter.Rules.PullRequests;
using Aibysitter.Web.GitHub;
using Microsoft.Extensions.Logging.Abstractions;
using static Aibysitter.Rules.Tests.PullRequests.PrTestData;
using Severity = Aibysitter.Rules.Severity;

namespace Aibysitter.Rules.Tests.GitHub;

/// <summary>Security review 2026-10-09, App mediums: items 6, 7, 8, 11, 12 (and 18).</summary>
public class SecurityMediumAppTests
{
    private static readonly PullRequestReviewer Reviewer = new();
    private static readonly PullRequestRef Pr = new(42, "o", "r", 7, "abcdef0123");

    private static CheckRunReport Build(RepoConfig config, IReadOnlyList<string>? notes, params ChangedFile[] files) =>
        CheckRunReport.Build(Reviewer.Review(new PullRequestContext(files, config)), Reviewer.Checks, files, config, [], notes);

    // Item 12 / 18: author text is inert in the summary and the comment.

    [Fact]
    public void Summary_EscapesPathsMessagesAndNotes()
    {
        var config = RepoConfig.Parse("{\"scope\": [\"src/**\"]}").Config;
        var removed = new ChangedFile("docs/a`b@x.md", FileChangeStatus.Removed);

        var report = Build(config, ["P014 skipped CLAUDE.md: symlink to <!-- @team [x](y)."], removed);

        Assert.Contains("- ``docs/a`b@x.md``: P004 ", report.Summary);
        Assert.DoesNotContain("<!--", report.Summary);
        Assert.DoesNotContain("@team", report.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("x.md):", report.Summary, StringComparison.Ordinal);
        Assert.Contains("\\<\\!-- @‍team \\[x\\]\\(y\\)", report.Summary);
    }

    [Fact]
    public void Comment_EscapesMessage_FencesPath()
    {
        var report = new CheckRunReport(ReviewConclusion.Neutral, "1 finding", "summary",
            [new CheckRunAnnotation("src/a`b.cs", 3, Severity.Error, "P001 Placeholder identifiers", "<details> @team **x**\n[l](u)", "")]);

        var body = ReviewComment.Build(report, Pr, 99);

        Assert.Contains("[``src/a`b.cs:3``](", body);
        Assert.Contains("P001 Placeholder identifiers: \\<details\\> @‍team \\*\\*x\\*\\* \\[l\\]\\(u\\)", body);
        Assert.DoesNotContain("<details>", body);
    }

    [Theory]
    [InlineData("plain", "`plain`")]
    [InlineData("a`b", "``a`b``")]
    [InlineData("`x", "`` `x ``")]
    [InlineData("a\nb", "`a b`")]
    public void Code_Fence(string value, string expected) => Assert.Equal(expected, GitHubMarkdown.Code(value));

    // Item 8: annotation text within GitHub's limits.

    [Fact]
    public void Annotation_TextClamped()
    {
        var a = OctokitGitHubGateway.ToOctokit(new CheckRunAnnotation("src/A.cs", 1, Severity.Error, new string('t', 300), new string('m', 70_000), new string('d', 70_000)));

        Assert.Equal(OctokitGitHubGateway.MaxAnnotationMessage, a.Message.Length);
        Assert.EndsWith("…", a.Message);
        Assert.Equal(OctokitGitHubGateway.MaxAnnotationTitle, a.Title.Length);
        Assert.Equal(OctokitGitHubGateway.MaxAnnotationDetails, a.RawDetails.Length);
    }

    [Fact]
    public void Clamp_KeepsSurrogatePairs()
    {
        var text = new string('a', 3) + "😀😀";

        Assert.Equal("aaa…", OctokitGitHubGateway.Clamp(text, 5));
        Assert.Equal("short", OctokitGitHubGateway.Clamp("short", 5));
    }

    // Item 7: summary within GitHub's limit.

    [Fact]
    public void Summary_ListsCapped_LengthCapped()
    {
        var config = RepoConfig.Parse("{\"scope\": [\"src/**\"]}").Config;
        var removed = Enumerable.Range(0, 5_000).Select(i => new ChangedFile($"docs/{new string('x', 200)}{i}.md", FileChangeStatus.Removed)).ToArray();
        var notes = Enumerable.Range(0, 5_000).Select(i => $"note {i} " + new string('n', 1_000)).ToList();

        var report = Build(config, notes, removed);

        Assert.True(report.Summary.Length <= CheckRunReport.MaxSummaryLength, $"{report.Summary.Length} characters");
        Assert.Contains("- and 4980 more", report.Summary);
        Assert.DoesNotContain("note 20 ", report.Summary);
    }

    [Fact]
    public void CapSummary_CutsAtLineEnd()
    {
        var summary = string.Join("\n", Enumerable.Range(0, 10_000).Select(i => $"line {i}"));

        var capped = CheckRunReport.CapSummary(summary);

        Assert.True(capped.Length <= CheckRunReport.MaxSummaryLength);
        Assert.EndsWith("\n\n" + CheckRunReport.TruncatedNote, capped);
        Assert.Matches(@"line \d+\n\n", capped[^(CheckRunReport.TruncatedNote.Length + 20)..]);
    }

    // Item 11: per-file and per-job byte limits, strict UTF-8, skipped files named.

    private static readonly PullRequestRef JobPr = new(1, "o", "r", 7, "abcdef0", "base000");

    private static async Task<CheckRunReport> ReviewAsync(FakeGitHubGateway fake, PullRequestReviewer? reviewer = null)
    {
        await new ReviewProcessor(fake, reviewer ?? new PullRequestReviewer(), NullLogger<ReviewProcessor>.Instance)
            .ProcessAsync(new ReviewJob(JobPr, 99, "d"), CancellationToken.None);
        return await fake.Completed.Task;
    }

    private static ChangedFile CsFile(string path) => Added(path, "[Fact]", "public void T() { }");

    [Fact]
    public async Task FileOver100KB_NotRead_Named()
    {
        var fake = new FakeGitHubGateway();
        fake.Files.Add(CsFile("src/Big.cs"));
        fake.RawContents["src/Big.cs"] = new byte[FileContent.MaxFileBytes + 1];

        var report = await ReviewAsync(fake);

        Assert.Contains("Not read \\(over 100 KB\\): src/Big.cs.", report.Summary);
    }

    [Fact]
    public async Task JobBudget_StopsAt2MB_Named()
    {
        var fake = new FakeGitHubGateway();
        for (var i = 0; i < 25; i++)
        {
            fake.Files.Add(CsFile($"src/F{i:00}.cs"));
            fake.RawContents[$"src/F{i:00}.cs"] = System.Text.Encoding.UTF8.GetBytes(new string('a', 90 * 1024));
        }

        var report = await ReviewAsync(fake);

        Assert.Contains("Not read \\(2 MB review limit reached\\): src/F22.cs, src/F23.cs, src/F24.cs.", report.Summary);
        Assert.Equal(25, fake.Calls.Count(c => c.StartsWith("content src/F", StringComparison.Ordinal)) + 2);
    }

    [Fact]
    public async Task NonUtf8File_NotRead_Named()
    {
        var fake = new FakeGitHubGateway();
        fake.Files.Add(CsFile("src/Latin.cs"));
        fake.RawContents["src/Latin.cs"] = [(byte)'c', 0xE9];

        var report = await ReviewAsync(fake);

        Assert.Contains("Not read \\(not UTF-8\\): src/Latin.cs.", report.Summary);
    }

    [Fact]
    public void ConfigNotDecoded_DefaultsAndConfigError()
    {
        var (config, errors) = ReviewProcessor.ParseConfig(new FileContent(null, 200_000, FileContent.TooLarge));

        Assert.Same(RepoConfig.Default, config);
        Assert.Equal(".github/aibysitter.json: over 100 KB; defaults used", Assert.Single(errors).Message);
    }

    // Item 6: a review error fails the check under a failing conclusion mode.

    private sealed class ThrowingCheck : IPullRequestCheck
    {
        public string Id => "P099";

        public string Title => "Throws";

        public Severity Severity => Severity.Info;

        public IEnumerable<PullRequestFinding> Evaluate(PullRequestContext context) => throw new InvalidOperationException("boom");
    }

    [Theory]
    [InlineData("{\"conclusion\": \"fail-on-errors\"}", ReviewConclusion.Failure)]
    [InlineData("{\"conclusion\": \"fail-on-warnings\"}", ReviewConclusion.Failure)]
    [InlineData("{\"conclusion\": \"advisory\"}", ReviewConclusion.Neutral)]
    [InlineData(null, ReviewConclusion.Neutral)]
    public async Task ReviewError_ConclusionFollowsMode(string? config, ReviewConclusion expected)
    {
        var fake = new FakeGitHubGateway();
        fake.Files.Add(Added("src/A.cs", "var a = 1;"));
        if (config is not null)
        {
            fake.BaseContents[RepoConfig.FilePath] = config;
        }

        var report = await ReviewAsync(fake, new PullRequestReviewer([new ThrowingCheck()]));

        Assert.Equal("Review failed", report.Title);
        Assert.Equal(expected, report.Conclusion);
    }

    [Fact]
    public async Task ErrorBeforeConfig_ReadsConfigInCatch()
    {
        var fake = new FakeGitHubGateway { ThrowOnFiles = new HttpRequestException("down") };
        fake.BaseContents[RepoConfig.FilePath] = "{\"conclusion\": \"fail-on-errors\"}";

        var report = await ReviewAsync(fake);

        Assert.Equal(ReviewConclusion.Failure, report.Conclusion);
    }

    [Fact]
    public async Task ErrorBeforeConfig_ConfigUnreadable_Failure()
    {
        var fake = new FakeGitHubGateway { ThrowOnFiles = new HttpRequestException("down"), ThrowOnBaseContent = new HttpRequestException("down") };

        var report = await ReviewAsync(fake);

        Assert.Equal(ReviewConclusion.Failure, report.Conclusion);
    }

    [Fact]
    public void NoConfigPaths_StayNeutral() =>
        Assert.Equal(ReviewConclusion.Neutral, CheckRunReport.ForError(new InvalidOperationException()).Conclusion);
}

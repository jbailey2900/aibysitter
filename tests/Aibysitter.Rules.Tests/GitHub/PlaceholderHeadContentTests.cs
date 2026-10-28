using Aibysitter.Rules.PullRequests;
using Aibysitter.Web.GitHub;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aibysitter.Rules.Tests.GitHub;

/// <summary>P001 on an edited template: the head content shows the guard outside the hunk (dogfood 5, an edited guarded template).</summary>
public class PlaceholderHeadContentTests
{
    private const string Path = "docs/deploy/sql/Setup-Backup.sql";
    private static readonly ReviewJob Job = new(new PullRequestRef(42, "o", "r", 7, "abcdef0123", "base0123456"), 99, "delivery-1");

    private static readonly string GuardedHead = string.Join("\n",
        "-- Fill in every ::NAME:: value in a copy of this file first.",
        "SET NOCOUNT ON;",
        "IF EXISTS (SELECT 1 FROM (VALUES (N'::DB_NAME::')) AS v(x) WHERE x LIKE N'%::%')",
        "BEGIN",
        "    RAISERROR(N'Fill in every placeholder value first.', 16, 1);",
        "    SET NOEXEC ON;",
        "END",
        "GO",
        "",
        "PRINT N'Backup';",
        "EXEC [::DB_NAME::].dbo.Report @Name = 'backup';",
        "");

    /// <summary>Lines 10–11 of the head: the guard (lines 3–5) is outside the hunk.</summary>
    private static ChangedFile Edit(string path = Path) =>
        new(path, FileChangeStatus.Modified, "@@ -10,1 +10,2 @@\n PRINT N'Backup';\n+EXEC [::DB_NAME::].dbo.Report @Name = 'backup';\n");

    private static async Task<(CheckRunReport Report, FakeGitHubGateway Fake)> Review(Action<FakeGitHubGateway> setup)
    {
        var fake = new FakeGitHubGateway();
        fake.BaseContents[RepoConfig.FilePath] = """{ "conclusion": "fail-on-warnings" }""";
        setup(fake);
        await new ReviewProcessor(fake, new PullRequestReviewer(), NullLogger<ReviewProcessor>.Instance).ProcessAsync(Job, CancellationToken.None);
        return (await fake.Completed.Task, fake);
    }

    private static bool HasP001(CheckRunReport report) => report.Annotations.Any(a => a.Title.StartsWith("P001", StringComparison.Ordinal));

    private static string Summary(CheckRunReport report) => report.Summary.Replace("\\", "", StringComparison.Ordinal);

    [Fact]
    public async Task EditedFile_GuardInHeadNotInHunk_NotFlagged()
    {
        var (report, fake) = await Review(f =>
        {
            f.Files.Add(Edit());
            f.Contents[Path] = GuardedHead;
        });

        Assert.Contains($"content {Path}", fake.Calls);
        Assert.False(HasP001(report));
        Assert.DoesNotContain(ReviewProcessor.DiffOnlyNotePrefix, report.Summary);
    }

    [Fact]
    public async Task EditedFile_NoGuardInHead_Flagged()
    {
        var (report, _) = await Review(f =>
        {
            f.Files.Add(Edit());
            f.Contents[Path] = "SET NOCOUNT ON;\n\n\n\n\n\n\n\nPRINT N'Backup';\nEXEC [::DB_NAME::].dbo.Report @Name = 'backup';\n";
        });

        Assert.Single(report.Annotations, a => a.Title.StartsWith("P001", StringComparison.Ordinal) && a.Line == 11);
    }

    [Fact]
    public async Task NewFileWithGuard_NotFlagged()
    {
        var lines = GuardedHead.Split('\n')[..^1];
        var added = new ChangedFile(Path, FileChangeStatus.Added, $"@@ -0,0 +1,{lines.Length} @@\n" + string.Concat(lines.Select(l => "+" + l + "\n")));

        var (report, _) = await Review(f => f.Files.Add(added));

        Assert.False(HasP001(report));
    }

    [Fact]
    public async Task FetchCut_ByCap_FlaggedWithNote()
    {
        var (report, fake) = await Review(f =>
        {
            for (var i = 0; i < ReviewProcessor.MaxContentFetches; i++)
            {
                f.Files.Add(new ChangedFile($"src/A{i:000}.cs", FileChangeStatus.Modified, "@@ -1,1 +1,1 @@\n+[Obsolete] class A { }"));
            }

            f.Files.Add(Edit());
            f.Contents[Path] = GuardedHead;
        });

        Assert.DoesNotContain($"content {Path}", fake.Calls);
        Assert.True(HasP001(report));
        Assert.Contains($"{ReviewProcessor.DiffOnlyNotePrefix}: {Path}.", Summary(report));
    }

    [Fact]
    public async Task NoPlaceholderOnAddedLine_NoFetch()
    {
        var (_, fake) = await Review(f => f.Files.Add(new ChangedFile(Path, FileChangeStatus.Modified, "@@ -10,1 +10,2 @@\n EXEC [::DB_NAME::].dbo.Report;\n+PRINT N'Done';\n")));

        Assert.DoesNotContain($"content {Path}", fake.Calls);
    }

    [Fact]
    public async Task P001IgnoredForPath_NoFetchNoNote()
    {
        var (report, fake) = await Review(f =>
        {
            f.BaseContents[RepoConfig.FilePath] = """{ "conclusion": "fail-on-warnings", "ignore": [{ "paths": ["docs/deploy/sql/*.sql"], "checks": ["P001"] }] }""";
            f.Files.Add(Edit());
        });

        Assert.DoesNotContain($"content {Path}", fake.Calls);
        Assert.False(HasP001(report));
        Assert.DoesNotContain(ReviewProcessor.DiffOnlyNotePrefix, report.Summary);
    }
}

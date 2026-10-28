using Aibysitter.Rules.PullRequests;
using static Aibysitter.Rules.Tests.PullRequests.PrTestData;

namespace Aibysitter.Rules.Tests.PullRequests;

/// <summary>False-positive shapes from dogfood 4, and the true positives next to them.</summary>
public class Dogfood4TuningTests
{
    private static List<PullRequestFinding> Run(IPullRequestCheck check, params ChangedFile[] files) => check.Evaluate(Context(files)).ToList();

    private static ChangedFile Modified(string path, string patch) => new(path, FileChangeStatus.Modified, patch);

    // P016: an exemption already on a removed line of the same hunk is not added.

    private const string RewrappedExemption = """
        @@ -22,7 +22,8 @@ public static WebApplication MapSubscribers(this WebApplication app)
                     return Results.Text("Unsubscribed.");
                 }).RequireRateLimiting(RateLimiting.Unsubscribe).DisableAntiforgery();

        -        app.MapPost(WebhookPath, HandleWebhookAsync).DisableAntiforgery();
        +        app.MapPost(WebhookPath, HandleWebhookAsync).DisableAntiforgery()
        +            .WithMetadata(new RequestSizeLimitAttribute(MaxWebhookBytes));
                 return app;
             }

        """;

    [Fact]
    public void P016_ExemptionOnRemovedLineOfSameHunk_NotFlagged() =>
        Assert.Empty(Run(new SecurityExemptions(), Modified("src/Subscribers/SubscriberEndpoints.cs", RewrappedExemption)));

    [Fact]
    public void P016_SecondExemptionAddedInSameHunk_Flagged()
    {
        var patch = """
            @@ -10,3 +10,4 @@
             var app = builder.Build();
            -app.MapPost("/a", A).DisableAntiforgery();
            +app.MapPost("/a", A).DisableAntiforgery().WithName("a");
            +app.MapPost("/b", B).DisableAntiforgery();
             app.Run();
            """;

        var finding = Assert.Single(Run(new SecurityExemptions(), Modified("src/Program.cs", patch)));
        Assert.Equal(12, finding.Line);
    }

    [Fact]
    public void P016_ExemptionRemovedInAnotherHunk_StillFlagged()
    {
        var patch = """
            @@ -5,3 +5,2 @@
             var app = builder.Build();
            -app.MapPost("/a", A).DisableAntiforgery();
             app.MapGet("/", Home);
            @@ -40,2 +39,3 @@
             app.MapGet("/x", X);
            +app.MapPost("/b", B).DisableAntiforgery();
             app.Run();
            """;

        var finding = Assert.Single(Run(new SecurityExemptions(), Modified("src/Program.cs", patch)));
        Assert.Equal(40, finding.Line);
    }

    [Fact]
    public void P016_DifferentExemptionOnRemovedLine_StillFlagged()
    {
        var patch = """
            @@ -10,2 +10,2 @@
             var app = builder.Build();
            -app.MapPost("/a", A).DisableAntiforgery();
            +app.MapPost("/a", A).DisableAntiforgery().AllowAnonymous();
            """;

        var finding = Assert.Single(Run(new SecurityExemptions(), Modified("src/Program.cs", patch)));
        Assert.Equal("Security exemption added: \".AllowAnonymous()\"", finding.Message);
    }

    // P001: a file that refuses to run while a placeholder is left is a template.

    private static readonly string[] GuardedSqlTemplate =
    [
        "-- One-time install, after replacing every ::PLACEHOLDER:: in the :setvar lines.",
        ":setvar BackupDirectory \"::STAGING_BACKUP_DIR::\"",
        ":setvar RunnerLogin \"::RUNNER_LOGIN::\"",
        "",
        "USE master;",
        "IF N'$(BackupDirectory)$(RunnerLogin)' LIKE N'%::%'",
        "    THROW 50000, N'Replace every ::PLACEHOLDER:: in the :setvar lines first.', 1;",
        "GO",
    ];

    [Fact]
    public void P001_FileThatThrowsOnPlaceholders_NotFlagged() =>
        Assert.Empty(Run(new PlaceholderIdentifiers(), Added("scripts/sql/staging-restore-proc.sql", GuardedSqlTemplate)));

    [Theory]
    [InlineData("scripts/setup.ps1", "$dir = '::BACKUP_DIR::'", "if ($dir -like '*::*') { throw 'Fill in the values first.' }")]
    [InlineData("scripts/setup.sh", "DIR=\"::BACKUP_DIR::\"", "if [[ \"$DIR\" == *\"::\"* ]]; then echo 'Fill in DIR'; exit 1; fi")]
    [InlineData("scripts/setup.sh", "DIR=\"::BACKUP_DIR::\"", "echo \"$DIR\" | grep -q '::' && exit 1")]
    public void P001_ScriptGuards_NotFlagged(string path, string value, string guard) =>
        Assert.Empty(Run(new PlaceholderIdentifiers(), Added(path, value, guard)));

    [Fact]
    public void P001_SameTemplateWithoutGuard_Flagged()
    {
        var lines = GuardedSqlTemplate.Where(l => !l.Contains("LIKE N'%::%'", StringComparison.Ordinal) && !l.Contains("THROW", StringComparison.Ordinal)).ToArray();

        Assert.Equal(2, Run(new PlaceholderIdentifiers(), Added("scripts/sql/staging-restore-proc.sql", lines)).Count);
    }

    [Fact]
    public void P001_TestWithoutStop_Flagged()
    {
        var file = Added("scripts/setup.sh", "DIR=\"::BACKUP_DIR::\"", "if [[ \"$DIR\" == *\"::\"* ]]; then echo 'warning: DIR not set'; fi");

        Assert.Single(Run(new PlaceholderIdentifiers(), file));
    }

    [Fact]
    public void P001_GuardInAnotherFile_DoesNotApply()
    {
        var guard = Added("scripts/check.sql", "IF N'$(X)' LIKE N'%::%'", "    THROW 50000, N'Fill in X.', 1;");
        var template = Added("scripts/install.sql", ":setvar X \"::INSTALL_DIR::\"");

        var finding = Assert.Single(Run(new PlaceholderIdentifiers(), guard, template));
        Assert.Equal("scripts/install.sql", finding.Path);
    }

    // P001 guard window: the stop may follow a multi-line condition, up to a blank line or GO, at most 10 lines.

    private static string[] MultiLineGuard(int conditionLines, bool blankBeforeThrow = false) =>
    [
        ":setvar BackupDirectory \"::STAGING_BACKUP_DIR::\"",
        ":setvar RunnerLogin \"::RUNNER_LOGIN::\"",
        "",
        "IF N'$(BackupDirectory)$(RunnerLogin)' LIKE N'%::%'",
        .. Enumerable.Repeat("    OR N'$(BackupDirectory)' = N''", conditionLines),
        .. (blankBeforeThrow ? new[] { "" } : []),
        "    THROW 50000, N'Fill in every :setvar value first.', 1;",
        "GO",
    ];

    [Fact]
    public void P001_MultiLineGuard_NotFlagged() =>
        Assert.Empty(Run(new PlaceholderIdentifiers(), Added("scripts/sql/staging-restore-proc.sql", MultiLineGuard(3))));

    [Fact]
    public void P001_GuardThenBlankLineThenThrow_Flagged() =>
        Assert.Equal(2, Run(new PlaceholderIdentifiers(), Added("scripts/sql/staging-restore-proc.sql", MultiLineGuard(3, blankBeforeThrow: true))).Count);

    [Fact]
    public void P001_ThrowPastTheWindow_Flagged()
    {
        Assert.Empty(Run(new PlaceholderIdentifiers(), Added("scripts/sql/a.sql", MultiLineGuard(PlaceholderIdentifiers.GuardWindow - 1))));
        Assert.Equal(2, Run(new PlaceholderIdentifiers(), Added("scripts/sql/a.sql", MultiLineGuard(PlaceholderIdentifiers.GuardWindow))).Count);
    }

    [Fact]
    public void P001_GuardThenGoThenThrow_Flagged()
    {
        var lines = MultiLineGuard(1).ToList();
        lines.Insert(lines.FindIndex(l => l.Contains("THROW", StringComparison.Ordinal)), "GO");

        Assert.Equal(2, Run(new PlaceholderIdentifiers(), Added("scripts/sql/a.sql", [.. lines])).Count);
    }

    [Fact]
    public void P001_ThrowMessageNamingAPlaceholder_IsNotAGuard()
    {
        var file = Added("scripts/install.sql",
            ":setvar X \"::INSTALL_DIR::\"",
            "IF N'$(X)' = N''",
            "    THROW 50000, N'Set ::INSTALL_DIR::.', 1;");

        Assert.Equal([1, 3], Run(new PlaceholderIdentifiers(), file).Select(f => f.Line));
    }

    // P004: the finding says how to proceed.

    [Fact]
    public void P004_Message_SaysWidenScopeOrSplit()
    {
        var config = RepoConfig.Parse("{\"scope\": [\"src/**\"]}").Config;
        var finding = Assert.Single(new OutOfScopeFiles().Evaluate(Context(config, Modified("README.md", "@@ -1 +1 @@\n-a\n+b"))));

        Assert.Equal("Outside declared scope (modified): README.md. Widen `scope` in a separate PR merged first, or move the file to its own PR.", finding.Message);
        Assert.Equal($"Add the path to \"scope\" in {RepoConfig.FilePath}, or move the change to a pull request of its own.", finding.FixHint);
    }

    // P001 head content (dogfood 5): the head decides the template rules on edited files.

    [Theory]
    [InlineData("docs/sql/setup.sql", FileChangeStatus.Modified, "@@ -5,1 +5,2 @@\n IF 1 = 1\n+EXEC [::DB_NAME::].dbo.Report;", true)]
    [InlineData("scripts/a.ps1", FileChangeStatus.Added, "@@ -0,0 +1,1 @@\n+$x = 'YOUR_KEY'", true)]
    [InlineData("docs/sql/setup.sql", FileChangeStatus.Modified, "@@ -5,2 +5,2 @@\n EXEC [::DB_NAME::].dbo.Report;\n-PRINT 1;\n+PRINT 2;", false)]
    [InlineData("tests/sql/SetupTests.cs", FileChangeStatus.Modified, "@@ -1,1 +1,1 @@\n+var db = \"::DB_NAME::\";", false)]
    [InlineData("docs/sql/setup.sql", FileChangeStatus.Removed, "@@ -1,1 +0,0 @@\n-EXEC [::DB_NAME::].dbo.Report;", false)]
    [InlineData("docs/setup.md", FileChangeStatus.Modified, "@@ -1,1 +1,1 @@\n+Set ::DB_NAME:: first.", false)]
    public void P001_NeedsHeadContent(string path, FileChangeStatus status, string patch, bool expected) =>
        Assert.Equal(expected, PlaceholderIdentifiers.NeedsHeadContent(new ChangedFile(path, status, patch)));
}

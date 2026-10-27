using Aibysitter.Rules.PullRequests;

namespace Aibysitter.Rules.Tests.PullRequests;

/// <summary>P008 justification lines in the PR description.</summary>
public class DeletedTestJustificationTests
{
    private const string Removed = "tests/Orders/OrderServiceTests.cs";

    private static ChangedFile RemovedFile(string path) => new(path, FileChangeStatus.Removed, "@@ -1 +0,0 @@\n-x");

    private static PullRequestContext Context(string? description, IReadOnlyCollection<string>? head = null, IReadOnlyDictionary<string, string>? workflows = null, RepoConfig? config = null, params ChangedFile[] files) =>
        new(files.Length == 0 ? [RemovedFile(Removed)] : files, config ?? RepoConfig.Default, HeadFiles: head ?? ["src/Orders/OrderService.cs", "tests/Orders/OrderTests.cs", "tools/smoke.ps1"], Description: description, WorkflowFiles: workflows);

    private static (List<PullRequestFinding> Findings, IReadOnlyList<string> Notes) Review(PullRequestContext context)
    {
        var review = new PullRequestReviewer([new DeletedTests()]).Review(context);
        return (review.Findings.ToList(), review.Notes ?? []);
    }

    [Fact]
    public void TestFileReplacement_DowngradedToInfo()
    {
        var (findings, notes) = Review(Context($"Moves the order tests.\n\nAibysitter: P008 {Removed} -> tests/Orders/OrderTests.cs\n"));

        var finding = Assert.Single(findings);
        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Equal($"Test file deleted: {Removed}. Replaced by tests/Orders/OrderTests.cs (test file).", finding.Message);
        Assert.Empty(notes);
    }

    [Fact]
    public void WorkflowReferencedReplacement_DowngradedToInfo_NamesTheWorkflow()
    {
        var workflows = new Dictionary<string, string> { [".github/workflows/ci.yml"] = "steps:\n  - run: pwsh tools/smoke.ps1\n" };

        var (findings, notes) = Review(Context($"  Aibysitter: P008 {Removed} -> tools/smoke.ps1", workflows: workflows));

        var finding = Assert.Single(findings);
        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Equal($"Test file deleted: {Removed}. Replaced by tools/smoke.ps1 (referenced by .github/workflows/ci.yml).", finding.Message);
        Assert.Empty(notes);
    }

    [Fact]
    public void WorkflowWithWindowsPath_Accepted()
    {
        var workflows = new Dictionary<string, string> { [".github/workflows/ci.yml"] = "steps:\n  - run: .\\tools\\smoke.ps1 -SelfTest\n" };

        var (findings, notes) = Review(Context($"Aibysitter: P008 {Removed} -> tools/smoke.ps1", workflows: workflows));

        Assert.Equal($"Test file deleted: {Removed}. Replaced by tools/smoke.ps1 (referenced by .github/workflows/ci.yml).", Assert.Single(findings).Message);
        Assert.Empty(notes);
    }

    [Theory]
    [InlineData("./tools/smoke.ps1")]
    [InlineData(".\\tools\\smoke.ps1")]
    [InlineData("tools\\smoke.ps1")]
    public void JustificationPath_Normalised(string replacement)
    {
        var workflows = new Dictionary<string, string> { [".github/workflows/ci.yml"] = "steps:\n  - run: pwsh tools/smoke.ps1\n" };

        var (findings, notes) = Review(Context($"Aibysitter: P008 {Removed} -> {replacement}", workflows: workflows));

        var finding = Assert.Single(findings);
        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Equal($"Test file deleted: {Removed}. Replaced by tools/smoke.ps1 (referenced by .github/workflows/ci.yml).", finding.Message);
        Assert.Empty(notes);
    }

    public static TheoryData<string, IReadOnlyCollection<string>?, string> Rejected() => new()
    {
        { $"Aibysitter: P008 {Removed} -> tests/Orders/Gone.cs", null, $"P008 justification for {Removed} not applied: tests/Orders/Gone.cs is not in the head commit." },
        { $"Aibysitter: P008 {Removed} -> src/Orders/OrderService.cs", null, $"P008 justification for {Removed} not applied: src/Orders/OrderService.cs is not a test file and no workflow under .github/workflows/ references it." },
        { "Aibysitter: P008 tests/Other/OtherTests.cs -> tests/Orders/OrderTests.cs", null, "P008 justification for tests/Other/OtherTests.cs not applied: no P008 finding for that path." },
    };

    [Theory]
    [MemberData(nameof(Rejected))]
    public void InvalidJustification_ErrorUnchanged_NoteNamesTheCondition(string description, IReadOnlyCollection<string>? head, string note)
    {
        var (findings, notes) = Review(Context(description, head));

        var finding = Assert.Single(findings);
        Assert.Null(finding.Severity);
        Assert.Equal($"Test file deleted: {Removed}", finding.Message);
        Assert.Equal([note], notes);
    }

    [Fact]
    public void HeadFileListUnavailable_ErrorUnchanged()
    {
        var context = new PullRequestContext([RemovedFile(Removed)], RepoConfig.Default, Description: $"Aibysitter: P008 {Removed} -> tests/Orders/OrderTests.cs");

        var (findings, notes) = Review(context);

        Assert.Null(Assert.Single(findings).Severity);
        Assert.Equal([$"P008 justification for {Removed} not applied: the head file list is unavailable."], notes);
    }

    [Fact]
    public void DuplicateLine_FirstApplies_SecondNoted()
    {
        var (findings, notes) = Review(Context($"Aibysitter: P008 {Removed} -> tests/Orders/OrderTests.cs\nAibysitter: P008 {Removed} -> tests/Orders/Gone.cs"));

        Assert.Equal(Severity.Info, Assert.Single(findings).Severity);
        Assert.Equal([$"P008 justification for {Removed} not applied: duplicate line for this path."], notes);
    }

    [Theory]
    [InlineData("Aibysitter: P009 tests/Orders/OrderServiceTests.cs -> tests/Orders/OrderTests.cs")]
    [InlineData("See Aibysitter: P008 tests/Orders/OrderServiceTests.cs -> tests/Orders/OrderTests.cs")]
    [InlineData("Aibysitter: P008 tests/Orders/OrderServiceTests.cs tests/Orders/OrderTests.cs")]
    [InlineData("- Aibysitter: P008 tests/Orders/OrderServiceTests.cs -> tests/Orders/OrderTests.cs")]
    public void NotAJustificationLine_Ignored(string description)
    {
        var (findings, notes) = Review(Context(description));

        Assert.Null(Assert.Single(findings).Severity);
        Assert.Empty(notes);
    }

    [Fact]
    public void FullyJustified_DoesNotFailUnderFailOnErrors()
    {
        var config = RepoConfig.Parse("{\"conclusion\": \"fail-on-errors\"}").Config;

        var review = new PullRequestReviewer([new DeletedTests()]).Review(Context($"Aibysitter: P008 {Removed} -> tests/Orders/OrderTests.cs", config: config));

        Assert.Equal(ReviewConclusion.Neutral, review.Conclusion);
    }

    [Fact]
    public void NoDescription_NoNotes() => Assert.Empty(Review(Context(null)).Notes);

    [Fact]
    public void FixHint_NamesTheLine() =>
        Assert.Equal("Restore the file, or add \"Aibysitter: P008 <removed test path> -> <replacement path>\" to the PR description.", Assert.Single(Review(Context(null)).Findings).FixHint);
}

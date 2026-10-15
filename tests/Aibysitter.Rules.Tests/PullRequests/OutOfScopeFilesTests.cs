using Aibysitter.Rules.PullRequests;
using static Aibysitter.Rules.Tests.PullRequests.PrTestData;

namespace Aibysitter.Rules.Tests.PullRequests;

public class OutOfScopeFilesTests
{
    private static readonly RepoConfig Scoped = RepoConfig.Parse("{\"scope\": [\"src/**\", \"tests/**\"]}").Config;

    private readonly OutOfScopeFiles check = new();

    [Fact]
    public void NoScopeDeclared_NoFindings()
    {
        Assert.Empty(check.Evaluate(Context(Added("anywhere/A.cs", "x"))));
    }

    [Fact]
    public void FlagsFilesOutsideScope_AtLine1()
    {
        var findings = check.Evaluate(Context(Scoped,
            Added("src/A.cs", "x"),
            Added("tests/ATests.cs", "x"),
            Added(".github/workflows/ci.yml", "x"),
            Added("README.md", "x"))).ToList();

        Assert.Equal(new[] { ".github/workflows/ci.yml", "README.md" }, findings.Select(f => f.Path));
        Assert.All(findings, f => Assert.Equal(1, f.Line));
        Assert.Contains("Add the path to \"scope\" in .github/aibysitter.json", findings[0].FixHint);
    }

    [Fact]
    public void RemovedFileOutsideScope_IsFlagged()
    {
        var file = new ChangedFile("docs/old.md", FileChangeStatus.Removed, "@@ -1,1 +0,0 @@\n-x");

        Assert.Contains("(removed)", Assert.Single(check.Evaluate(Context(Scoped, file))).Message);
    }

    [Theory]
    [InlineData("src/New.cs", "docs/Old.cs", true)]
    [InlineData("docs/New.cs", "src/Old.cs", true)]
    [InlineData("src/New.cs", "src/Old.cs", false)]
    public void Renames_CheckBothPaths(string path, string previous, bool flagged)
    {
        var file = new ChangedFile(path, FileChangeStatus.Renamed, PreviousPath: previous);

        Assert.Equal(flagged, check.Evaluate(Context(Scoped, file)).Any());
    }
}

using Aibysitter.Rules.PullRequests;
using static Aibysitter.Rules.Tests.PullRequests.PrTestData;

namespace Aibysitter.Rules.Tests.PullRequests;

public class SecretsInDiffTests
{
    private readonly SecretsInDiff _check = new();

    [Fact]
    public void AddedSecret_AnyFileType_Flagged_Redacted()
    {
        var token = "gh" + "p_" + new string('k', 36);

        var findings = _check.Evaluate(Context(
            Added("src/A.cs", "var t = \"" + token + "\";"),
            Added(".env.example", "DB=Server=db;Password=Hunter2Prod;"),
            Added("docs/setup.md", "Use " + token))).ToList();

        Assert.Equal(new[] { "src/A.cs", ".env.example", "docs/setup.md" }, findings.Select(f => f.Path));
        Assert.All(findings, f => Assert.DoesNotContain("kkkkkkkk", f.Message));
        Assert.Contains("GitHub token (ghp_…)", findings[0].Message);
    }

    [Fact]
    public void RemovedLinesAndRemovedFiles_NotFlagged()
    {
        var patch = "@@ -1,1 +1,1 @@\n-Password=Hunter2Prod;\n+Password=${DB_PASSWORD};";

        Assert.Empty(_check.Evaluate(Context(
            new ChangedFile("a.config", FileChangeStatus.Modified, patch),
            new ChangedFile("b.config", FileChangeStatus.Removed, "@@ -1,1 +0,0 @@\n-Password=Hunter2Prod;"))));
    }
}

public class SkippedTestsTests
{
    private readonly SkippedTests _check = new();

    [Theory]
    [InlineData("tests/A.cs", "[Fact(Skip = \"flaky\")]")]
    [InlineData("tests/A.cs", "[Theory(Skip=\"later\")]")]
    [InlineData("tests/A.cs", "[Ignore(\"broken\")]")]
    [InlineData("tests/A.cs", "[Test, Ignore(\"x\")]")]
    [InlineData("tests/A.cs", "[TestMethod, Ignore]")]
    [InlineData("tests/A.cs", "[Test, Category(\"slow\"), Ignore(\"x\")]")]
    [InlineData("src/a.test.ts", "it.skip('works', () => {")]
    [InlineData("src/a.test.ts", "describe.skip('suite', () => {")]
    [InlineData("tests/ui/a.spec.js", "test.skip(\"share\", async ({ page }) => {")]
    [InlineData("tests/ui/a.spec.js", "test.skip(`share ${n}`, async ({ page }) => {")]
    [InlineData("tests/ui/a.spec.js", "test.describe.skip('share', () => {")]
    [InlineData("tests/ui/a.spec.js", "  test.skip();")]
    [InlineData("tests/ui/a.spec.js", "  test.skip(true, 'not ready');")]
    [InlineData("src/a.test.js", "xit('works', () => {")]
    [InlineData("src/a.test.js", "xtest('works', () => {")]
    [InlineData("tests/test_a.py", "@pytest.mark.skip(reason=\"flaky\")")]
    [InlineData("tests/test_a.py", "    pytest.skip(\"later\")")]
    [InlineData("tests/test_a.py", "@unittest.skip(\"later\")")]
    [InlineData("a_test.go", "\tt.Skip(\"flaky\")")]
    [InlineData("a_test.go", "\tt.Skipf(\"%s\", x)")]
    [InlineData("src/lib.rs", "#[ignore]")]
    [InlineData("src/ATest.java", "@Disabled")]
    public void Flags(string path, string line)
    {
        Assert.Single(_check.Evaluate(Context(Added(path, line))));
    }

    [Theory]
    [InlineData("tests/A.cs", "[JsonIgnore]")]
    [InlineData("tests/A.cs", "[IgnoreDataMember]")]
    [InlineData("src/A.cs", "cfg.ForMember(d => d.Id, opt => opt.Ignore());")]
    [InlineData("src/A.cs", "items[i].Ignore();")]
    [InlineData("tests/A.cs", "[InlineData(\"src/A.cs\", \"opt => opt.Ignore()\")]")]
    [InlineData("src/A.cs", "var x = map[key].Ignore(opt);")]
    [InlineData("tests/test_a.py", "@pytest.mark.skipif(sys.platform == \"win32\", reason=\"posix\")")]
    [InlineData("tests/test_a.py", "@unittest.skipIf(IS_WINDOWS, \"posix\")")]
    [InlineData("src/a.ts", "process.exit(1)")]
    [InlineData("src/a.ts", "const skip = items.skip(3)")]
    [InlineData("README.md", "Use it.skip( to skip a test.")]
    [InlineData("tests/ui/share-banner.spec.js", "  test.skip(hasShare, 'This browser has the Web Share API; the fallback is not shown.');")]
    [InlineData("tests/ui/a.spec.js", "  test.skip(({ browserName }) => browserName === 'webkit', 'no clipboard');")]
    [InlineData("tests/ui/a.spec.js", "  test.skip(!process.env.IQC_TEST_SQL, 'needs SQL Server');")]
    [InlineData("tests/ui/a.spec.js", "  test.skip(browserName === 'firefox');")]
    public void DoesNotFlag(string path, string line)
    {
        Assert.Empty(_check.Evaluate(Context(Added(path, line))));
    }
}

public class SuppressedDiagnosticsTests
{
    private readonly SuppressedDiagnostics _check = new();

    [Theory]
    [InlineData("src/A.cs", "#pragma warning disable CS8602")]
    [InlineData("src/A.cs", "[SuppressMessage(\"Style\", \"IDE0060\")]")]
    [InlineData("src/A.cs", "[assembly: SuppressMessage(\"x\", \"y\")]")]
    [InlineData("src/A.cs", "// ReSharper disable once UnusedMember.Global")]
    [InlineData("src/A.csproj", "<NoWarn>CS1591</NoWarn>")]
    [InlineData(".editorconfig", "dotnet_diagnostic.CA2007.severity = none")]
    [InlineData("src/a.ts", "// eslint-disable-next-line no-console")]
    [InlineData("src/a.ts", "/* eslint-disable */")]
    [InlineData("src/a.ts", "// @ts-ignore")]
    [InlineData("src/a.ts", "// @ts-nocheck")]
    [InlineData("src/a.py", "import os  # noqa: F401")]
    [InlineData("src/a.py", "x = f()  # type: ignore")]
    [InlineData("src/a.py", "# pylint: disable=broad-except")]
    [InlineData("src/a.py", "x = f()  # pyright: ignore")]
    [InlineData("src/a.go", "x := f() //nolint:errcheck")]
    [InlineData("src/lib.rs", "#[allow(dead_code)]")]
    [InlineData("src/lib.rs", "#![allow(clippy::all)]")]
    [InlineData("src/A.java", "@SuppressWarnings(\"unchecked\")")]
    public void Flags(string path, string line)
    {
        Assert.Single(_check.Evaluate(Context(Added(path, line))));
    }

    [Theory]
    [InlineData("src/A.cs", "#pragma warning restore CS8602")]
    [InlineData("src/a.ts", "// @ts-expect-error intentional")]
    [InlineData(".editorconfig", "dotnet_diagnostic.CA2007.severity = warning")]
    [InlineData("docs/a.md", "Avoid #pragma warning disable.")]
    public void DoesNotFlag(string path, string line)
    {
        Assert.Empty(_check.Evaluate(Context(Added(path, line))));
    }
}

public class CommittedArtifactsTests
{
    private readonly CommittedArtifacts _check = new();

    [Theory]
    [InlineData("node_modules/left-pad/index.js", "Dependency folder (node_modules)")]
    [InlineData("web/node_modules/x/package.json", "Dependency folder (node_modules)")]
    [InlineData("src/Api/bin/Debug/net10.0/Api.dll", ".NET build output")]
    [InlineData("src/Api/bin/Release/net10.0/Api.pdb", ".NET build output")]
    [InlineData("src/Api/obj/Debug/net10.0/Api.AssemblyInfo.cs", ".NET build output")]
    [InlineData("src/Api/obj/project.assets.json", ".NET build output")]
    [InlineData("src/Api/obj/Api.csproj.nuget.g.props", ".NET build output")]
    [InlineData("pkg/__pycache__/a.cpython-312.pyc", "Python bytecode cache")]
    [InlineData("pkg/a.pyc", "Python bytecode cache")]
    [InlineData(".env", "Environment file")]
    [InlineData("api/.env.local", "Environment file")]
    [InlineData(".env.production", "Environment file")]
    public void Classifies(string path, string kind)
    {
        Assert.Equal(kind, CommittedArtifacts.Classify(path));
    }

    [Theory]
    [InlineData("bin/rails")]
    [InlineData("tools/bin/cli.js")]
    [InlineData("assets/obj/model.obj")]
    [InlineData(".env.example")]
    [InlineData(".env.sample")]
    [InlineData(".env.template")]
    [InlineData(".env.dist")]
    [InlineData("src/environment.ts")]
    [InlineData("docs/node_modules.md")]
    public void DoesNotClassify(string path)
    {
        Assert.Null(CommittedArtifacts.Classify(path));
    }

    [Fact]
    public void OnlyNewPaths_Flagged()
    {
        var findings = _check.Evaluate(Context(
            new ChangedFile(".env", FileChangeStatus.Added),
            new ChangedFile("bin/Debug/a.dll", FileChangeStatus.Modified),
            new ChangedFile("obj/Release/b.dll", FileChangeStatus.Removed),
            new ChangedFile("node_modules/x.js", FileChangeStatus.Renamed, PreviousPath: "vendor/x.js"))).ToList();

        Assert.Equal(new[] { ".env", "node_modules/x.js" }, findings.Select(f => f.Path));
        Assert.All(findings, f => Assert.Equal(1, f.Line));
    }
}

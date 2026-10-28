using Aibysitter.Rules.PullRequests;
using static Aibysitter.Rules.Tests.PullRequests.PrTestData;

namespace Aibysitter.Rules.Tests.PullRequests;

public class FileKindsTestFileTests
{
    [Theory]
    [InlineData("tests/Orders/OrderServiceTests.cs")]
    [InlineData("src/Orders.Tests/OrderServiceTests.cs")]
    [InlineData("src/OrderServiceTest.java")]
    [InlineData("src/order.test.ts")]
    [InlineData("src/order.spec.js")]
    [InlineData("src/__tests__/order.js")]
    [InlineData("pkg/test_order.py")]
    [InlineData("pkg/order_test.py")]
    [InlineData("order_test.go")]
    [InlineData("spec/order_spec.rb")]
    public void IsTestFile(string path) => Assert.True(FileKinds.IsTestFile(path));

    [Theory]
    [InlineData("src/Orders/OrderService.cs")]
    [InlineData("src/Testing.md")]
    [InlineData("tests/fixtures/data.json")]
    [InlineData("src/latest.ts")]
    [InlineData("src/contest.py")]
    public void IsNotTestFile(string path) => Assert.False(FileKinds.IsTestFile(path));
}

public class DeletedTestsTests
{
    private readonly DeletedTests _check = new();

    [Fact]
    public void RemovedTestFile_Flagged_OthersNot()
    {
        var findings = _check.Evaluate(Context(
            new ChangedFile("tests/OrderTests.cs", FileChangeStatus.Removed),
            new ChangedFile("src/Billing.cs", FileChangeStatus.Removed),
            new ChangedFile("tests/OtherTests.cs", FileChangeStatus.Modified),
            new ChangedFile("tests/NewTests.cs", FileChangeStatus.Renamed, PreviousPath: "tests/OldTests.cs"))).ToList();

        Assert.Equal("tests/OrderTests.cs", Assert.Single(findings).Path);
    }
}

public class SwallowedExceptionsTests
{
    private readonly SwallowedExceptions _check = new();

    [Theory]
    [InlineData("src/A.cs", new[] { "try { Save(); } catch { }" })]
    [InlineData("src/A.cs", new[] { "try { Save(); }", "catch (Exception) { }" })]
    [InlineData("src/A.cs", new[] { "catch (Exception ex)", "{", "}" })]
    [InlineData("src/A.cs", new[] { "catch (IOException) when (retry)", "{", "    // ignore", "}" })]
    [InlineData("src/a.ts", new[] { "} catch (e) {}" })]
    [InlineData("src/a.js", new[] { "} catch {", "  /* nothing */", "}" })]
    [InlineData("src/A.java", new[] { "} catch (IOException e) {", "}" })]
    [InlineData("src/a.py", new[] { "except:", "    pass" })]
    [InlineData("src/a.py", new[] { "except Exception as e:  # noqa", "    pass" })]
    [InlineData("src/a.py", new[] { "except ValueError: pass" })]
    public void Flags(string path, string[] lines)
    {
        var finding = Assert.Single(_check.Evaluate(Context(Added(path, lines))));

        Assert.Equal(lines.ToList().FindIndex(l => l.Contains("catch") || l.Contains("except")) + 1, finding.Line);
    }

    [Theory]
    [InlineData("src/A.cs", new[] { "catch (Exception ex) { logger.LogError(ex, \"x\"); throw; }" })]
    [InlineData("src/A.cs", new[] { "catch (Exception ex)", "{", "    logger.LogError(ex, \"x\");", "}" })]
    [InlineData("src/a.ts", new[] { "} catch (e) { report(e); }" })]
    [InlineData("src/a.py", new[] { "except KeyError:", "    return None" })]
    [InlineData("src/a.py", new[] { "except KeyError:", "    log.warning('x')", "    pass" })]
    [InlineData("docs/a.md", new[] { "catch { }" })]
    [InlineData("src/A.cs", new[] { "// catch-all handler below" })]
    [InlineData("src/A.cs", new[] { "catch (OperationCanceledException)", "{", "}" })]
    [InlineData("src/A.cs", new[] { "catch (TaskCanceledException) when (stopping.IsCancellationRequested) { }" })]
    public void DoesNotFlag(string path, string[] lines)
    {
        Assert.Empty(_check.Evaluate(Context(Added(path, lines))));
    }

    [Fact]
    public void BlockSplitByUnchangedLine_NotJoined()
    {
        var patch = "@@ -1,2 +1,3 @@\n+catch (Exception)\n {\n+}";

        Assert.Empty(_check.Evaluate(Context(new ChangedFile("src/A.cs", FileChangeStatus.Modified, patch))));
    }
}

public class NewDependenciesTests
{
    private readonly NewDependencies _check = new();

    private List<PullRequestFinding> Run(string path, string patch) =>
        _check.Evaluate(Context(new ChangedFile(path, FileChangeStatus.Modified, patch))).ToList();

    [Fact]
    public void Csproj_NewPackage_Flagged_VersionBump_NotFlagged()
    {
        var patch = "@@ -1,3 +1,4 @@\n <ItemGroup>\n-    <PackageReference Include=\"Serilog.AspNetCore\" Version=\"9.0.0\" />\n+    <PackageReference Include=\"Serilog.AspNetCore\" Version=\"10.0.0\" />\n+    <PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\" />\n </ItemGroup>";

        var finding = Assert.Single(Run("src/Web/Web.csproj", patch));

        Assert.Equal("New dependency: Newtonsoft.Json", finding.Message);
        Assert.Equal(3, finding.Line);
    }

    [Fact]
    public void DirectoryPackagesProps_PackageVersionAlone_Info()
    {
        var finding = Assert.Single(Run("Directory.Packages.props", "@@ -1,1 +1,2 @@\n <ItemGroup>\n+  <PackageVersion Include=\"Polly\" Version=\"8.4.0\" />"));

        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Equal("Version pinned (transitive): Polly", finding.Message);
    }

    [Fact]
    public void PackageJson_DependencySections_Flagged_ScriptsAndVersion_NotFlagged()
    {
        var patch = string.Join("\n",
            "@@ -1,10 +1,14 @@",
            " {",
            "-  \"version\": \"1.0.0\",",
            "+  \"version\": \"1.1.0\",",
            "   \"scripts\": {",
            "+    \"lint\": \"eslint .\",",
            "+    \"prebuild\": \"1.0\",",
            "   },",
            "   \"dependencies\": {",
            "-    \"react\": \"^18.2.0\",",
            "+    \"react\": \"^19.0.0\",",
            "+    \"lodash\": \"^4.17.21\",",
            "   },",
            "   \"devDependencies\": {",
            "+    \"@types/lodash\": \"^4.17.0\"",
            "   }",
            " }");

        Assert.Equal(new[] { "New dependency: lodash", "New dependency: @types/lodash" }, Run("package.json", patch).Select(f => f.Message));
    }

    [Fact]
    public void PackageJson_HunkStartsMidSection_UsesVersionShape()
    {
        var patch = "@@ -20,2 +20,4 @@\n     \"react\": \"^19.0.0\",\n+    \"zod\": \"^3.23.0\",\n+    \"dev\": \"vite\",\n   },";

        Assert.Equal("New dependency: zod", Assert.Single(Run("web/package.json", patch)).Message);
    }

    [Fact]
    public void PackageJson_SecondHunk_ResetsSection()
    {
        var patch = "@@ -1,3 +1,3 @@\n   \"dependencies\": {\n-    \"a\": \"^1.0.0\",\n+    \"a\": \"^1.1.0\",\n@@ -40,2 +40,3 @@\n   \"x\": 1,\n+  \"build\": \"tsc\",";

        Assert.Empty(Run("package.json", patch));
    }

    [Fact]
    public void Requirements_NewLines_Flagged_CommentsAndPinsChanges_NotFlagged()
    {
        var patch = "@@ -1,2 +1,5 @@\n-requests==2.31.0\n+requests==2.32.0\n+httpx[http2]>=0.27\n+# comment\n+-r base.txt";

        Assert.Equal("New dependency: httpx", Assert.Single(Run("requirements-dev.txt", patch)).Message);
    }

    [Fact]
    public void GoMod_RequireLines_Flagged()
    {
        var patch = "@@ -3,3 +3,5 @@\n require (\n+\tgithub.com/spf13/cobra v1.8.0\n+require golang.org/x/sync v0.7.0\n )";

        Assert.Equal(new[] { "New dependency: github.com/spf13/cobra", "New dependency: golang.org/x/sync" }, Run("go.mod", patch).Select(f => f.Message));
    }

    [Fact]
    public void OtherFiles_Ignored()
    {
        Assert.Empty(Run("src/A.cs", "@@ -1,1 +1,2 @@\n x\n+<PackageReference Include=\"Foo\" />"));
    }
}

public class CiConfigEditedTests
{
    private readonly CiConfigEdited _check = new();

    [Theory]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData(".github/actions/setup/action.yml")]
    [InlineData(".gitlab-ci.yml")]
    [InlineData("azure-pipelines.yml")]
    [InlineData(".circleci/config.yml")]
    [InlineData("Jenkinsfile")]
    [InlineData("build/Jenkinsfile")]
    [InlineData("bitbucket-pipelines.yml")]
    public void IsCiConfig(string path) => Assert.True(CiConfigEdited.IsCiConfig(path));

    [Theory]
    [InlineData(".github/aibysitter.json")]
    [InlineData(".github/CODEOWNERS")]
    [InlineData("docs/workflows/ci.md")]
    public void IsNotCiConfig(string path) => Assert.False(CiConfigEdited.IsCiConfig(path));

    [Fact]
    public void AnyStatus_Flagged_IncludingRenameAway()
    {
        var findings = _check.Evaluate(Context(
            new ChangedFile(".github/workflows/ci.yml", FileChangeStatus.Removed),
            new ChangedFile("old/ci.yml", FileChangeStatus.Renamed, PreviousPath: ".github/workflows/ci.yml"),
            new ChangedFile("src/A.cs", FileChangeStatus.Modified))).ToList();

        Assert.Equal(2, findings.Count);
        Assert.Equal("CI config removed: .github/workflows/ci.yml", findings[0].Message);
    }
}

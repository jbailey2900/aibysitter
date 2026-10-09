using System.Text.RegularExpressions;
using Aibysitter.Rules.Tests.Parity;

namespace Aibysitter.Rules.Tests;

public class ReleaseWorkflowTests
{
    private static string WorkflowsDir => Path.Combine(NodeRunner.RepoRoot, ".github", "workflows");

    private static string ReleaseCli => File.ReadAllText(Path.Combine(WorkflowsDir, "release-cli.yml"));

    [Fact]
    public void ServiceImages_PinnedByDigest()
    {
        var images = Directory.GetFiles(WorkflowsDir, "*.yml")
            .SelectMany(f => File.ReadLines(f).Where(l => l.TrimStart().StartsWith("image:", StringComparison.Ordinal)).Select(l => (File: Path.GetFileName(f), Line: l.Trim())))
            .ToList();

        Assert.NotEmpty(images);
        Assert.All(images, i => Assert.Matches(@"^image: \S+:\S+@sha256:[0-9a-f]{64}$", i.Line));
    }

    [Fact]
    public void ReleaseCli_RunsOnCliTagsOnly()
    {
        Assert.Contains("on:\n  push:\n    tags: [\"cli-v*\"]\n", ReleaseCli, StringComparison.Ordinal);
        Assert.DoesNotContain("pull_request", ReleaseCli, StringComparison.Ordinal);
        Assert.DoesNotContain("branches:", ReleaseCli, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseCli_ReadOnlyToken() =>
        Assert.Contains("permissions:\n  contents: read\n\n", ReleaseCli, StringComparison.Ordinal);

    [Fact]
    public void ReleaseCli_ChecksTagAgainstMainAndVersion()
    {
        Assert.Contains("git merge-base --is-ancestor \"$GITHUB_SHA\" origin/main", ReleaseCli, StringComparison.Ordinal);
        Assert.Contains("[ \"cli-v$version\" = \"$GITHUB_REF_NAME\" ]", ReleaseCli, StringComparison.Ordinal);
    }

    private static string Step(string name) =>
        Regex.Match(ReleaseCli, @"- name: " + Regex.Escape(name) + @"\n(?<body>(?:        .*\n)+)").Groups["body"].Value;

    [Fact]
    public void ReleaseCli_JobMayRequestOidcToken() =>
        Assert.Contains("  release:\n    runs-on: ubuntu-latest\n    permissions:\n      contents: read\n      id-token: write\n", ReleaseCli, StringComparison.Ordinal);

    [Fact]
    public void ReleaseCli_PushesOnlyWhenPublic_WithTrustedPublishingKey()
    {
        var login = Step("NuGet login");
        var push = Step("Push to nuget.org");

        Assert.Contains("id: login", login, StringComparison.Ordinal);
        Assert.Contains("if: ${{ !github.event.repository.private }}", login, StringComparison.Ordinal);
        Assert.Matches(@"uses: NuGet/login@[0-9a-f]{40} ", login);
        Assert.Contains("user: jbailey2900", login, StringComparison.Ordinal);
        Assert.Contains("if: ${{ !github.event.repository.private }}", push, StringComparison.Ordinal);
        Assert.Contains("NUGET_API_KEY: ${{ steps.login.outputs.NUGET_API_KEY }}", push, StringComparison.Ordinal);
        Assert.Contains("--api-key \"$NUGET_API_KEY\"", push, StringComparison.Ordinal);
        Assert.True(ReleaseCli.IndexOf("- name: NuGet login", StringComparison.Ordinal) < ReleaseCli.IndexOf("- name: Push to nuget.org", StringComparison.Ordinal));
        Assert.DoesNotMatch(@"secrets\.", ReleaseCli);
    }

    [Theory]
    [InlineData("action-test.yml")]
    [InlineData("ci.yml")]
    [InlineData("deploy-scripts.yml")]
    [InlineData("deploy.yml")]
    [InlineData("release-cli.yml")]
    public void Workflow_PinsEveryActionBySha(string file)
    {
        var uses = Regex.Matches(File.ReadAllText(Path.Combine(WorkflowsDir, file)), @"uses:\s*(?<ref>\S+)")
            .Select(m => m.Groups["ref"].Value)
            .Where(r => !r.StartsWith("./", StringComparison.Ordinal))
            .ToList();

        Assert.All(uses, r => Assert.Matches(@"@[0-9a-f]{40}$", r));
    }

    [Fact]
    public void WorkflowList_IsCovered() =>
        Assert.Equal(
            ["action-test.yml", "ci.yml", "deploy-scripts.yml", "deploy.yml", "release-cli.yml"],
            Directory.GetFiles(WorkflowsDir, "*.yml").Select(Path.GetFileName).Order(StringComparer.Ordinal));
}

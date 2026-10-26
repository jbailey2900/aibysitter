using System.Text.RegularExpressions;
using Aibysitter.Rules.Tests.Parity;

namespace Aibysitter.Rules.Tests;

public class ContributingTests
{
    private static string TemplatesDir => Path.Combine(NodeRunner.RepoRoot, ".github", "ISSUE_TEMPLATE");

    private static string Template(string file) => File.ReadAllText(Path.Combine(TemplatesDir, file));

    /// <summary>Ids of body fields with "required: true", in file order.</summary>
    private static List<string> RequiredIds(string yaml) =>
        Regex.Split(yaml, @"\n  - type: ")
            .Skip(1)
            .Where(f => f.Contains("required: true", StringComparison.Ordinal))
            .Select(f => Regex.Match(f, @"\n    id: (?<id>\S+)").Groups["id"].Value)
            .ToList();

    [Fact]
    public void Templates_AreBugFalsePositiveFeature_WithConfig() =>
        Assert.Equal(
            ["bug.yml", "config.yml", "false-positive.yml", "feature.yml"],
            Directory.GetFiles(TemplatesDir).Select(Path.GetFileName).Order(StringComparer.Ordinal));

    [Theory]
    [InlineData("bug.yml", "name: Bug\n", "surface,steps,expected,actual")]
    [InlineData("false-positive.yml", "name: False positive\n", "rule,line,why,surface")]
    [InlineData("feature.yml", "name: Feature\n", "what")]
    public void Template_NameAndRequiredFields(string file, string name, string required)
    {
        var yaml = Template(file);

        Assert.StartsWith(name, yaml, StringComparison.Ordinal);
        Assert.Equal(required.Split(','), RequiredIds(yaml));
    }

    [Fact]
    public void Config_DisablesBlankIssues_AndLinksPrivateReporting()
    {
        var yaml = Template("config.yml");

        Assert.Contains("blank_issues_enabled: false", yaml, StringComparison.Ordinal);
        Assert.Contains("https://github.com/jbailey2900/aibysitter/security/advisories/new", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void RepoConfig_IgnoresDocsAndTestFixturesForP005_AndContributingSaysWhy()
    {
        var (config, errors) = Aibysitter.Rules.PullRequests.RepoConfig.Parse(File.ReadAllText(Path.Combine(NodeRunner.RepoRoot, ".github", "aibysitter.json")));

        Assert.Empty(errors);
        Assert.True(config.IsEnabled("P005"));
        Assert.True(config.IsIgnored("P005", "tests/Aibysitter.Rules.Tests/SecretsInRulesFileTests.cs"));
        Assert.False(config.IsIgnored("P001", "tests/Aibysitter.Rules.Tests/SecretsInRulesFileTests.cs"));
        Assert.True(config.IsIgnored("P001", "src/Aibysitter.Rules/PullRequests/PullRequestCheckDocs.cs"));
        Assert.False(config.IsIgnored("P005", "src/Aibysitter.Web/Program.cs"));
        Assert.Contains("`.github/aibysitter.json` ignores the check docs", File.ReadAllText(Path.Combine(NodeRunner.RepoRoot, "CONTRIBUTING.md")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dotnet test Aibysitter.slnx")]
    [InlineData("AIBYSITTER_TEST_SQL")]
    [InlineData("`Aibysitter review`")]
    [InlineData("measurement note")]
    [InlineData("False positive issue template")]
    [InlineData("the `cli-version` default in `action.yml` in the same pull request")]
    [InlineData("after the CLI version is on nuget.org, tag `v<major>.<minor>.<patch>`")]
    [InlineData("The release notes give the full 40-character commit SHA to pin, as a `uses:` line")]
    [InlineData("`dotnet-version` default in `action.yml` is an SDK version published and installable")]
    public void Contributing_Covers(string text) =>
        Assert.Contains(text, File.ReadAllText(Path.Combine(NodeRunner.RepoRoot, "CONTRIBUTING.md")), StringComparison.Ordinal);
}

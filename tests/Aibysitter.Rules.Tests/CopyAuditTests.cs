using Microsoft.AspNetCore.Mvc.Testing;

namespace Aibysitter.Rules.Tests;

/// <summary>Copy audit 2026-10-09: statements corrected to match the code.</summary>
public class CopyAuditTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private static string Repo(params string[] path) =>
        File.ReadAllText(Path.Combine([Parity.NodeRunner.RepoRoot, .. path])).ReplaceLineEndings("\n");

    [Theory]
    [InlineData("/GitHub", "Under <code>fail-on-warnings</code> or <code>fail-on-errors</code>, any config error fails the check.")]
    [InlineData("/GitHub", "It reads the pull request's head commit, and the config file from the base commit, with fixed caps:")]
    [InlineData("/GitHub", "<span class=\"t-prompt\">conclusion</span> failure")]
    [InlineData("/GitHub", "seeded with one example of each check, under <code>fail-on-warnings</code>.</figcaption>")]
    [InlineData("/GitHub/Config", "Commit it to the default branch. A pull request that adds or changes it takes effect after it merges.")]
    public async Task Page_HasCorrectedCopy(string path, string expected) =>
        Assert.Contains(expected, await factory.CreateClient().GetStringAsync(path));

    [Theory]
    [InlineData("/GitHub", "under the advisory setting")]
    [InlineData("/GitHub", "head commit only")]
    [InlineData("/GitHub/Config", "or to a pull request")]
    public async Task Page_DropsOldCopy(string path, string old) =>
        Assert.DoesNotContain(old, await factory.CreateClient().GetStringAsync(path));

    [Fact]
    public void InstallDoc_GateFirstExamples_LiveCheckIds()
    {
        var doc = Repo("docs", "installing-on-your-repos.md");

        Assert.Contains("Under `advisory` (the default), every review completes as `success` (no findings) or `neutral` (findings). Under `fail-on-warnings` or `fail-on-errors`, a blocking finding completes it as `failure`.", doc);
        Assert.DoesNotContain("Nothing blocks a merge.", doc);
        Assert.DoesNotContain("\"conclusion\": \"advisory\"", doc);
        Assert.Contains("Starter config:\n\n```json\n{\n  \"conclusion\": \"fail-on-warnings\",\n  \"disable\": [\"P011\"]\n}\n```", doc);
        Assert.Contains("Check IDs (P001–P019 except the withdrawn P003, P012, P017) skip that check.", doc);
    }

    [Fact]
    public void Readme_R008Threshold_SmokeProject()
    {
        var readme = Repo("README.md");

        Assert.Contains($"| R008 | EmphasisInflation (over {Rules.EmphasisInflation.PerHundredLines} per 100 lines, at least {Rules.EmphasisInflation.MinAllowed} allowed) | Warning |", readme);
        Assert.Contains("| `tests/Aibysitter.Web.Smoke` |", readme);
        Assert.Contains("Smoke tests are skipped unless `AIBYSITTER_SMOKE_URL` is set", readme);
    }

    [Fact]
    public void Contributing_ListsEveryIgnoreEntry() =>
        Assert.Contains("`tests/**` for P005, and the P001 source for P001", Repo("CONTRIBUTING.md"));
}

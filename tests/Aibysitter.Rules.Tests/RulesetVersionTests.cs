using System.Security.Cryptography;
using System.Text;
using Aibysitter.Rules.Browser;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Aibysitter.Rules.Tests;

public class RulesetVersionTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    /// <summary>
    /// SHA-256 of the browser export (patterns, rules and severities, scoring, limits) for <see cref="PinnedVersion"/>.
    /// A change here means a rule or scoring change: bump RulesetVersion.Current, add a changelog entry, then re-pin both.
    /// </summary>
    private const int PinnedVersion = 4;
    private const string PinnedFingerprint = "cf645e17df5f5e4a9c76838e05cce00e4096ba233af1c1eb1000f17dab076d7b";

    [Fact]
    public void Export_MatchesPinnedFingerprint_ForCurrentVersion()
    {
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(PatternExport.Build())));

        Assert.Equal((PinnedVersion, PinnedFingerprint), (RulesetVersion.Current, fingerprint));
    }

    [Fact]
    public void Changelog_HasEntryForCurrentVersion_NewestFirst()
    {
        var versions = RulesetChangelog.Ruleset.Select(e => e.Version!.Value).ToList();

        Assert.Equal(RulesetVersion.Current, versions[0]);
        Assert.Equal(versions.OrderDescending(), versions);
        Assert.All(RulesetChangelog.AppChecks, e => Assert.Null(e.Version));
    }

    [Fact]
    public async Task ChangelogPage_ListsRulesetAndAppChecks()
    {
        var html = await factory.CreateClient().GetStringAsync("/Rules/Changelog");

        Assert.Contains($"Current ruleset: v{RulesetVersion.Current}.", html);
        Assert.Contains("<h3>v1, First versioned ruleset</h3>", html);
        Assert.Contains("<h2>App checks</h2>", html);
        Assert.Contains("<h3>R006 MissingIdentifiers (#24)</h3>", html);
    }

    [Theory]
    [InlineData("/Lint?sample=true")]
    [InlineData("/Gallery/go-http-service")]
    public async Task ScorePanel_ShowsRulesetVersion(string path)
    {
        var html = await factory.CreateClient().GetStringAsync(path);

        Assert.Contains($"<p class=\"note\">Ruleset <a href=\"/Rules/Changelog\">v{RulesetVersion.Current}</a></p>", html);
    }

    [Fact]
    public async Task Registry_HasRulesetVersion()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(await factory.CreateClient().GetStringAsync("/registry.json"));

        Assert.Equal(RulesetVersion.Current, doc.RootElement.GetProperty("rulesetVersion").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public void BrowserExport_CarriesRulesetVersion()
    {
        Assert.Contains($"\"rulesetVersion\": {RulesetVersion.Current},", PatternExport.Build());
    }

    [Fact]
    public async Task ChangelogPage_ShowsV2First()
    {
        var html = await factory.CreateClient().GetStringAsync("/Rules/Changelog");

        Assert.True(html.IndexOf("<h3>v2,", StringComparison.Ordinal) < html.IndexOf("<h3>v1,", StringComparison.Ordinal));
        Assert.Contains("R016 ManualCursorRule (Info)", html);
    }

    [Theory]
    [InlineData("/Rules/R016", "Apply Manually")]
    [InlineData("/Rules/R016", "Cursor rules (.mdc)")]
    [InlineData("/Rules/R015", "href=\"/Rules/R016\">R016</a> (Info)")]
    [InlineData("/Rules", "href=\"/Rules/Changelog\"")]
    public async Task R016Pages(string path, string expected)
    {
        Assert.Contains(expected, await factory.CreateClient().GetStringAsync(path));
    }
}

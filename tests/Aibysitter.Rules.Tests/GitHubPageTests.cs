using Aibysitter.Rules.PullRequests;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Aibysitter.Rules.Tests;

public class GitHubPageTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task GitHubPage_ListsChecks_ConfigAndComingSoon()
    {
        var html = await factory.CreateClient().GetStringAsync("/GitHub");

        foreach (var doc in PullRequestCheckDocs.All)
        {
            Assert.Contains($"<a href=\"/Rules/{doc.Id}\">{doc.Id}</a>", html);
        }

        Assert.DoesNotContain("coming soon", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fail-on-errors", html);
        Assert.Contains("CI config modified: .github/workflows/ci.yml", html);
    }

    [Fact]
    public async Task ScopeIsReadOnlyByP004_OnPageAndInstallDoc()
    {
        const string sentence = "Read only by P004; other checks review every changed file.";
        var html = await factory.CreateClient().GetStringAsync("/GitHub");

        Assert.Contains(sentence, html);
        Assert.DoesNotContain("tests without assertions", html);
        Assert.Contains(sentence, File.ReadAllText(Path.Combine(Parity.NodeRunner.RepoRoot, "docs", "installing-on-your-repos.md")));
    }

    private const string GateSentence = "A pull request that adds an unpinned action, a new dependency, a leaked key or a security exemption then cannot merge until a human looks.";

    [Fact]
    public async Task GitHubPage_LeadsWithTheGate()
    {
        var html = await factory.CreateClient().GetStringAsync("/GitHub");
        var hero = html[html.IndexOf("<section class=\"hero\">", StringComparison.Ordinal)..html.IndexOf("</section>", StringComparison.Ordinal)];

        Assert.Contains("<h1>Gate agent pull requests.</h1>", hero);
        Assert.Contains(GateSentence, hero);
        Assert.Contains("<pre id=\"gate-config\"><code>{\n  \"conclusion\": \"fail-on-warnings\"\n}</code></pre>", hero);
        Assert.Contains("data-copy=\"gate-config\"", hero);
        Assert.Contains("<code>fail-on-errors</code> is the lighter setting", hero);
        Assert.Contains("<code>advisory</code> is the default and the trial mode", hero);
        Assert.Contains("a bot bumping a dependency is a change a human should sign off on", hero);
        Assert.Matches(@"<script type=""module"" src=""/js/copy\.[^""]*mjs", html);
    }

    private static readonly string[] WhatWeStore =
    [
        "Code is fetched from GitHub for the duration of one review and discarded. Diffs, file contents and finding text are never written to disk, database or logs.",
        "Results go back to GitHub only: the check run, its annotations and summary, and the PR comment when {0} is on.",
        "Stored per review: a daily count by conclusion. A queue file holding the repo, PR number and commit SHAs exists only while the review runs and is deleted when it finishes.",
        "Logs keep the repo owner and name, PR number, short SHA, delivery and check-run IDs, finding count and any error text for 14 days.",
    ];

    [Fact]
    public async Task WhatWeStore_SameText_BetweenModesAndConfigReference()
    {
        var html = await factory.CreateClient().GetStringAsync("/GitHub");
        var page = html[html.IndexOf("</section>", StringComparison.Ordinal)..html.IndexOf("<h2>Configuration</h2>", StringComparison.Ordinal)];
        page = page[page.IndexOf("<h2>What we store</h2>", StringComparison.Ordinal)..];

        var doc = File.ReadAllText(Path.Combine(Parity.NodeRunner.RepoRoot, "docs", "installing-on-your-repos.md")).ReplaceLineEndings("\n");
        var section = doc[doc.IndexOf("## Install", StringComparison.Ordinal)..doc.IndexOf("## Config file", StringComparison.Ordinal)];
        section = section[section.IndexOf("## What we store", StringComparison.Ordinal)..];

        foreach (var line in WhatWeStore)
        {
            Assert.Contains($"<li>{string.Format(line, "<code>comment</code>")}</li>", page);
            Assert.Contains($"- {string.Format(line, "`comment`")}\n", section);
        }
    }

    [Fact]
    public async Task SecurityModel_ListsPermissionsFromInstallDoc()
    {
        var html = await factory.CreateClient().GetStringAsync("/GitHub");
        var section = html[html.IndexOf("<h2>Security model</h2>", StringComparison.Ordinal)..];
        section = section[..section.IndexOf("</section>", StringComparison.Ordinal)];

        Assert.Contains("<li>Permissions: Checks (read and write), Pull requests (read and write; write is used only for the <code>comment</code> option), Contents (read). It cannot push, merge, or change settings.</li>", section);
        Assert.DoesNotContain("only write permission", section);
        Assert.Contains("Checks (read and write), Pull requests (read and write), Contents (read)", File.ReadAllText(Path.Combine(Parity.NodeRunner.RepoRoot, "docs", "installing-on-your-repos.md")));
    }

    [Fact]
    public async Task InstallSection_LinksInstallDoc()
    {
        var html = await factory.CreateClient().GetStringAsync("/GitHub");
        var install = html[html.IndexOf("<h2>Install</h2>", StringComparison.Ordinal)..];
        install = install[..install.IndexOf("</section>", StringComparison.Ordinal)];

        Assert.Contains("<a href=\"https://github.com/jbailey2900/aibysitter/blob/main/docs/installing-on-your-repos.md\">Step-by-step install guide</a>", install);
        Assert.True(File.Exists(Path.Combine(Parity.NodeRunner.RepoRoot, Aibysitter.Web.GitHub.GitHubAppLinks.InstallDocPath)));
    }

    [Fact]
    public void InstallDoc_LeadsWithTheGate()
    {
        var doc = File.ReadAllText(Path.Combine(Parity.NodeRunner.RepoRoot, "docs", "installing-on-your-repos.md")).ReplaceLineEndings("\n");
        var lead = doc[..doc.IndexOf("## Install", StringComparison.Ordinal)];

        Assert.Contains(GateSentence, lead);
        Assert.Contains("```json\n{\n  \"conclusion\": \"fail-on-warnings\"\n}\n```", lead);
        Assert.Contains("`fail-on-errors` is the lighter setting", lead);
        Assert.Contains("`advisory` is the default and the trial mode", lead);
        Assert.Contains("a bot bumping a dependency is a change a human should sign off on", lead);
    }

    [Fact]
    public void Readme_GitHubApp_LeadsWithTheGate()
    {
        var readme = File.ReadAllText(Path.Combine(Parity.NodeRunner.RepoRoot, "README.md")).ReplaceLineEndings("\n");
        var section = readme[readme.IndexOf("## GitHub App", StringComparison.Ordinal)..readme.IndexOf("## API", StringComparison.Ordinal)];
        var lead = section[..section.IndexOf("| ID |", StringComparison.Ordinal)];

        Assert.Contains(GateSentence, lead);
        Assert.Contains("```json\n{\n  \"conclusion\": \"fail-on-warnings\"\n}\n```", lead);
        Assert.Contains("`fail-on-errors` is the lighter setting", lead);
        Assert.Contains("`advisory` is the default and the trial mode", lead);
        Assert.Contains("a bot bumping a dependency is a change a human should sign off on", lead);
        Assert.DoesNotContain("\"conclusion\": \"fail-on-errors\"", section);
    }

    [Theory]
    [InlineData("P015")]
    [InlineData("P010")]
    [InlineData("P005")]
    [InlineData("P016")]
    public void GateExamples_FailUnderFailOnWarnings(string id)
    {
        var check = PullRequestReviewer.DiscoverChecks().Single(c => c.Id == id);

        Assert.True(PullRequestReviewer.Fails(id, check.Severity, ConclusionMode.FailOnWarnings));
        Assert.Equal(ConclusionMode.FailOnWarnings, RepoConfig.Parse("{\n  \"conclusion\": \"fail-on-warnings\"\n}").Config.Conclusion);
    }

    [Fact]
    public async Task CheckRunName_IsAibysitterReview_OnPageAndPrivacy()
    {
        var client = factory.CreateClient();

        Assert.Equal("Aibysitter review", Aibysitter.Web.GitHub.GitHubOptions.CheckRunName);
        Assert.Contains("It posts a check named Aibysitter review, with", await client.GetStringAsync("/GitHub"));
        Assert.Contains("<td>Creating and completing the Aibysitter review check run</td>", await client.GetStringAsync("/Privacy"));
    }

    [Fact]
    public async Task CommentKey_OnGitHubPage_PullRequestsWrite_OnPrivacy()
    {
        var client = factory.CreateClient();

        Assert.Contains("<td><code>comment</code></td>", await client.GetStringAsync("/GitHub"));
        Assert.Contains("<td>Read and write</td>\n            <td>Listing changed files and diffs; posting and updating one comment", await client.GetStringAsync("/Privacy"));
    }

    [Fact]
    public async Task GitHubPage_LinksToInstall_InHeroAndInstallSection()
    {
        var html = await factory.CreateClient().GetStringAsync("/GitHub");

        Assert.Equal(2, html.Split("href=\"https://github.com/apps/aibysitter/installations/new\"").Length - 1);
        Assert.Contains(">Install the GitHub App</a>", html);
    }

    [Fact]
    public async Task NoPage_SaysTheAppIsInTesting()
    {
        var client = factory.CreateClient();
        foreach (var path in new[] { "/", "/GitHub", "/About", "/llms.txt" })
        {
            var html = await client.GetStringAsync(path);
            Assert.DoesNotContain("in testing", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("coming soon", html, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Nav_LinksToGitHubPage()
    {
        Assert.Contains("href=\"/GitHub\">github</a>", await factory.CreateClient().GetStringAsync("/"));
    }

    [Fact]
    public async Task Configuration_DocumentsDisable()
    {
        var html = await factory.CreateClient().GetStringAsync("/GitHub");

        Assert.Contains("<td><code>disable</code></td>", html);
    }
}

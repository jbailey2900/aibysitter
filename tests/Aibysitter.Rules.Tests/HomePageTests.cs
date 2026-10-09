using System.Net;
using Aibysitter.Rules.PullRequests;
using Aibysitter.Web.Samples;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Aibysitter.Rules.Tests;

public class HomePageTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Home_RendersRealLintRunOfSample()
    {
        var html = await factory.CreateClient().GetStringAsync("/");
        var engine = new LintEngine();
        var findings = engine.Lint(SampleRules.Text);
        var score = engine.Score(findings);

        Assert.Contains("Lint result:</span> CLAUDE.md", html);
        Assert.DoesNotContain("aibysitter lint", html);
        Assert.Contains($"{findings.Count} findings, Aibysitter lint score {score.Value}/100 ({score.Grade})", html);
        foreach (var finding in findings)
        {
            Assert.Contains($"CLAUDE.md:{finding.Line}", html);
        }
    }

    [Fact]
    public async Task Home_ListsRuleAndPullRequestChecks()
    {
        var html = await factory.CreateClient().GetStringAsync("/");

        foreach (var doc in RuleDocs.All)
        {
            Assert.Contains($"href=\"/Rules/{doc.Id}\"", html);
        }

        foreach (var check in PullRequestReviewer.DiscoverChecks())
        {
            Assert.Contains($"<a href=\"/Rules/{check.Id}\">{check.Id}</a> {check.Title}", html);
        }
    }

    [Fact]
    public async Task Home_HasStatusStrip()
    {
        var html = await factory.CreateClient().GetStringAsync("/");

        Assert.Contains(@"<a href=""/Lint"">Browser linter</a> <span>Available now</span>", html);
        Assert.Contains(@"<a href=""/GitHub"">GitHub App</a> <span>Available now</span>", html);
        Assert.Contains(@"<a href=""/Hooks"">CLI and Action</a> <span>Available now</span>", html);
        Assert.DoesNotContain("Planned", html);
    }

    [Fact]
    public async Task Home_SplitsChecks_R006UnderTheApp()
    {
        var html = await factory.CreateClient().GetStringAsync("/");
        var rules = Section(html, "Rules files, available now", "Pull requests, GitHub App");
        var app = Section(html, "Pull requests, GitHub App", "</ul>");

        Assert.DoesNotContain("/Rules/R006", rules);
        Assert.Contains("/Rules/R001", rules);
        Assert.Contains(@"<a href=""/Rules/R006"">R006</a>", app);
        Assert.Contains("App-only", app);
    }

    [Fact]
    public async Task Home_ScoringCopy_MatchesModel()
    {
        var html = await factory.CreateClient().GetStringAsync("/");

        Assert.Contains("How the Aibysitter lint score works", html);
        Assert.Contains("Error 10, Warning 4, Info 1. One rule costs at most 30.", html);
        Assert.Contains("Error 40, Warning 30, Info 10. The lowest possible score is 20.", html);
    }

    private static string Section(string html, string start, string end)
    {
        var from = html.IndexOf(start, StringComparison.Ordinal);
        var to = html.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        return html[from..to];
    }

    [Fact]
    public void LintDemo_IsComputedOnceAndCached()
    {
        var demo = factory.Services.GetRequiredService<LintDemo>();

        Assert.Same(demo, factory.Services.GetRequiredService<LintDemo>());
        Assert.Same(demo.Result, demo.Result);
        Assert.NotEmpty(demo.Result.Lines);
    }

    [Theory]
    [InlineData("/fonts/jetbrains-mono-latin-400-normal.woff2", "font/woff2")]
    [InlineData("/fonts/jetbrains-mono-latin-700-normal.woff2", "font/woff2")]
    [InlineData("/fonts/OFL.txt", "text/plain")]
    public async Task FontFiles_AreServedFromSite(string path, string contentType)
    {
        var response = await factory.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(contentType, response.Content.Headers.ContentType?.MediaType);
    }
}

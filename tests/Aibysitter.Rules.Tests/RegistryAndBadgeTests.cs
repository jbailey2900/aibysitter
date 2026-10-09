using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using Aibysitter.Web.Gallery;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Aibysitter.Rules.Tests;

public class RegistryAndBadgeTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Registry_ListsEveryEntry_WithSchemaVersionAndAbsoluteUrls()
    {
        var catalog = factory.Services.GetRequiredService<GalleryCatalog>();
        var response = await factory.CreateClient().GetAsync("/registry.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("public, max-age=300", response.Headers.CacheControl?.ToString());

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, doc.RootElement.GetProperty("schemaVersion").GetInt32());
        var entries = doc.RootElement.GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal(catalog.All.Count, entries.Count);

        var go = entries.Single(e => e.GetProperty("id").GetString() == "go-http-service");
        var expected = catalog.Find("go-http-service")!;
        Assert.Equal(expected.Score.Value, go.GetProperty("score").GetInt32());
        Assert.Equal(expected.Score.Grade, go.GetProperty("grade").GetString());
        Assert.Equal("AGENTS.md", go.GetProperty("file").GetString());
        Assert.Equal("CC0-1.0", go.GetProperty("license").GetString());
        Assert.Equal(expected.Lines.Count, go.GetProperty("lines").GetInt32());
        Assert.Equal("https://aibysitting.net/Gallery/go-http-service", go.GetProperty("pageUrl").GetString());
        Assert.Equal("https://aibysitting.net/gallery/go-http-service/AGENTS.md", go.GetProperty("downloadUrl").GetString());
        Assert.Equal("https://aibysitting.net/gallery/go-http-service/badge.svg", go.GetProperty("badgeUrl").GetString());
    }

    [Fact]
    public async Task Badge_IsWellFormedSvg_WithGradeAndScore()
    {
        var response = await factory.CreateClient().GetAsync("/gallery/python-fastapi/badge.svg");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/svg+xml", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("public, max-age=3600", response.Headers.CacheControl?.ToString());
        var svg = XDocument.Parse(body).Root!;
        Assert.Equal("svg", svg.Name.LocalName);
        Assert.Contains(svg.Descendants(), e => e.Name.LocalName == "text" && e.Value == "A 100");
        Assert.Contains(svg.Descendants(), e => e.Name.LocalName == "text" && e.Value == "aibysitter");
        Assert.Equal($"Aibysitter lint score: A 100 (ruleset v{RulesetVersion.Current})", svg.Attribute("aria-label")?.Value);
        Assert.Equal($"Aibysitter lint score: A 100 (ruleset v{RulesetVersion.Current})", svg.Descendants().Single(e => e.Name.LocalName == "title").Value);
        Assert.DoesNotContain(svg.Descendants(), e => e.Name.LocalName == "text" && e.Value.Contains("ruleset", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/Gallery", "<span class=\"score-label\">Aibysitter lint score</span>")]
    [InlineData("/Gallery/python-fastapi", "<h2>Aibysitter lint score</h2>")]
    [InlineData("/Gallery/python-fastapi", "alt=\"Aibysitter lint score: A 100\"")]
    [InlineData("/Rules", "<h2>Aibysitter lint score</h2>")]
    public async Task Scores_AreLabelled(string path, string expected)
    {
        var html = await factory.CreateClient().GetStringAsync(path);

        Assert.Contains(expected, html);
    }

    [Fact]
    public async Task Badge_UnknownEntry_Returns404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().GetAsync("/gallery/nope/badge.svg")).StatusCode);
    }

    [Theory]
    [InlineData(95, "A", "#2e7d32")]
    [InlineData(82, "B", "#2e7d32")]
    [InlineData(76, "C", "#9a6400")]
    [InlineData(61, "D", "#9a6400")]
    [InlineData(10, "F", "#b3261e")]
    public void ScoreBadge_ColorsByGrade_AndWidensWithText(int value, string grade, string color)
    {
        var svg = XDocument.Parse(ScoreBadge.Render(new LintScore(value, grade, new Dictionary<string, int>(), new Dictionary<Severity, SeverityDeduction>()))).Root!;

        Assert.Contains(svg.Descendants(), e => e.Name.LocalName == "rect" && (string?)e.Attribute("fill") == color);
        Assert.True(int.Parse((string)svg.Attribute("width")!) > 90);
    }

    [Fact]
    public async Task EntryPage_ShowsBadgeAndMarkdownSnippet()
    {
        var html = await factory.CreateClient().GetStringAsync("/Gallery/monorepo-root");

        Assert.Contains("<img src=\"/gallery/monorepo-root/badge.svg\"", html);
        Assert.Contains("[![Aibysitter lint score: A 100](https://aibysitting.net/gallery/monorepo-root/badge.svg)](https://aibysitting.net/Gallery/monorepo-root)", html);
    }
}

using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Aibysitter.Rules.PullRequests;
using Aibysitter.Web.Gallery;
using Aibysitter.Web.Incidents;
using Aibysitter.Web.Notes;
using Aibysitter.Web.Seo;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Aibysitter.Rules.Tests;

public class SeoEndpointsTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Base = "https://aibysitting.net";
    private static readonly XNamespace Ns = "http://www.sitemaps.org/schemas/sitemap/0.9";

    private async Task<(HttpResponseMessage Response, string Body)> Get(string path)
    {
        var response = await factory.CreateClient().GetAsync(path);
        return (response, await response.Content.ReadAsStringAsync());
    }

    private async Task<List<XElement>> SiteMapUrls() =>
        XDocument.Parse((await Get("/sitemap.xml")).Body).Root!.Elements(Ns + "url").ToList();

    [Theory]
    [InlineData("/sitemap.xml", "application/xml")]
    [InlineData("/robots.txt", "text/plain")]
    [InlineData("/llms.txt", "text/plain")]
    public async Task Served_WithTypeAndCaching(string path, string mediaType)
    {
        var (response, _) = await Get(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(mediaType, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
        Assert.Equal(SeoEndpoints.CacheControl, response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task SiteMap_IsValidXml_DeclaresUtf8()
    {
        var (_, body) = await Get("/sitemap.xml");

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", body);
        Assert.Equal(Ns + "urlset", XDocument.Parse(body).Root!.Name);
    }

    [Fact]
    public async Task SiteMap_ListsFixedPages_RulesChecks_Gallery_Notes_Incidents()
    {
        var locs = (await SiteMapUrls()).Select(u => u.Element(Ns + "loc")!.Value).ToList();
        var expected = SiteMap.FixedPages
            .Concat(RuleDocs.All.Concat(PullRequestCheckDocs.All).Select(d => "/Rules/" + d.Id))
            .Concat(factory.Services.GetRequiredService<GalleryCatalog>().All.Select(e => "/Gallery/" + e.Id))
            .Concat(factory.Services.GetRequiredService<NoteCatalog>().All.Select(n => "/Notes/" + n.Slug))
            .Concat(factory.Services.GetRequiredService<IncidentCatalog>().All.OrderBy(i => i.Id).Select(i => "/Incidents/" + i.Id))
            .Select(p => Base + p);

        Assert.Equal(expected, locs);
        Assert.Equal(locs.Count, locs.Distinct().Count());
        Assert.DoesNotContain(locs, l => l.EndsWith("/source", StringComparison.Ordinal) || l.Contains("/Error", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SiteMap_LastModOnNotesOnly()
    {
        var notes = factory.Services.GetRequiredService<NoteCatalog>().All;
        var withDate = (await SiteMapUrls()).Where(u => u.Element(Ns + "lastmod") is not null)
            .ToDictionary(u => u.Element(Ns + "loc")!.Value, u => u.Element(Ns + "lastmod")!.Value);

        Assert.Equal(notes.ToDictionary(n => $"{Base}/Notes/{n.Slug}", n => n.Date.ToString("yyyy-MM-dd")), withDate);
    }

    [Fact]
    public async Task EverySiteMapUrl_Returns200()
    {
        var client = factory.CreateClient();
        foreach (var loc in (await SiteMapUrls()).Select(u => u.Element(Ns + "loc")!.Value))
        {
            var response = await client.GetAsync(loc[Base.Length..]);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{loc}: {(int)response.StatusCode}");
        }
    }

    [Fact]
    public async Task Robots_AllowsAll_DisallowsNonPages_PointsToSiteMap()
    {
        var (_, body) = await Get("/robots.txt");

        Assert.Equal(
            "User-agent: *\nAllow: /\nDisallow: /api/\nDisallow: /github/webhook\nDisallow: /health\n\nSitemap: https://aibysitting.net/sitemap.xml\n",
            body);
    }

    [Fact]
    public void Robots_DisallowsNoSiteMapPage()
    {
        var paths = factory.Services.GetRequiredService<SiteMap>().Entries.Select(e => e.Path);

        Assert.DoesNotContain(paths, p => SeoEndpoints.Disallowed.Any(d => p.StartsWith(d, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task LlmsTxt_Shape_VersionAndEveryRule()
    {
        var (_, body) = await Get("/llms.txt");

        Assert.StartsWith("# aibysitter\n\n> ", body);
        Assert.Contains($"Ruleset version: {RulesetVersion.Current}.", body);
        Assert.Contains("\n## Tools\n", body);
        Assert.Contains("\n## Optional\n", body);
        foreach (var doc in RuleDocs.All.Concat(PullRequestCheckDocs.All))
        {
            Assert.Contains($"- [{doc.Id} {doc.Name}]({Base}/Rules/{doc.Id}): {doc.Summary}\n", body);
        }
    }

    [Fact]
    public async Task LlmsTxt_EveryLink_Returns200()
    {
        var client = factory.CreateClient();
        var links = Regex.Matches((await Get("/llms.txt")).Body, @"\]\((https://[^)]+)\)").Select(m => m.Groups[1].Value).ToList();

        Assert.NotEmpty(links);
        Assert.Equal(Web.Seo.LlmsTxt.ExternalLinks, links.Where(l => !l.StartsWith(Base + "/", StringComparison.Ordinal)));
        foreach (var link in links.Except(Web.Seo.LlmsTxt.ExternalLinks))
        {
            var response = await client.GetAsync(link[Base.Length..]);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{link}: {(int)response.StatusCode}");
        }
    }

    [Fact]
    public async Task Endpoints_UseConfiguredBaseUrl()
    {
        var client = factory.WithWebHostBuilder(b => b.UseSetting("Site:BaseUrl", "https://example.test")).CreateClient();

        Assert.Contains("<loc>https://example.test/</loc>", await client.GetStringAsync("/sitemap.xml"));
        Assert.Contains("Sitemap: https://example.test/sitemap.xml", await client.GetStringAsync("/robots.txt"));
        Assert.Contains("(https://example.test/Lint)", await client.GetStringAsync("/llms.txt"));
    }

    [Fact]
    public async Task LlmsTxt_ListsCliAndAction()
    {
        var body = (await Get("/llms.txt")).Body;

        Assert.Contains("- [CLI](https://www.nuget.org/packages/Aibysitter.Cli): `dotnet tool install --global Aibysitter.Cli`", body);
        Assert.Contains("- [GitHub Action](https://github.com/marketplace/actions/aibysitter-rules-lint): `jbailey2900/aibysitter@v1`", body);
    }
}

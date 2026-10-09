using System.Xml.Linq;

namespace Aibysitter.Web.Smoke;

[Collection(SmokeCollection.Name)]
[Trait("Category", "Smoke")]
public class LayoutTests(SmokeFixture site)
{
    /// <summary>Layout: every sitemap page fits 375 px.</summary>
    [SmokeFact]
    public async Task EveryContentPage_Fits375()
    {
        var sitemap = await site.Api.GetAsync("/sitemap.xml");
        Assert.Equal(200, sitemap.Status);
        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
        var paths = XDocument.Parse(await sitemap.TextAsync()).Descendants(ns + "loc").Select(l => new Uri(l.Value).PathAndQuery).ToList();
        Assert.NotEmpty(paths);

        await using var s = await site.SessionAsync(375, 812);
        var wide = new List<string>();
        foreach (var path in paths)
        {
            var response = await s.Page.GotoAsync(path);
            Assert.True(response?.Status == 200, $"{path}: {response?.Status}");
            var width = await s.Page.EvaluateAsync<int>("document.documentElement.scrollWidth");
            if (width != 375)
            {
                wide.Add($"{path} ({width})");
            }
        }

        Assert.True(wide.Count == 0, $"Wider than 375 px of {paths.Count} pages: " + string.Join(", ", wide));
    }
}

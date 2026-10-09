using System.Text.Json;
using Aibysitter.Rules;
using Aibysitter.Rules.PullRequests;

namespace Aibysitter.Web.Smoke;

[Collection(SmokeCollection.Name)]
[Trait("Category", "Smoke")]
public class RouteTests(SmokeFixture site)
{
    /// <summary>E-03.</summary>
    [SmokeFact]
    public async Task UnknownPath_Is404StatusPage_NoIndex()
    {
        await using var s = await site.SessionAsync();

        var response = await s.Page.GotoAsync("/no-such-page");

        Assert.Equal(404, response!.Status);
        Assert.Equal("Page not found.", await s.Page.Locator("main h1").InnerTextAsync());
        Assert.Equal("noindex", await s.Page.Locator("meta[name=robots]").GetAttributeAsync("content"));
    }

    /// <summary>R-02, R-04.</summary>
    [SmokeFact]
    public async Task RulePages_LiveIds200_WithdrawnIds404()
    {
        var live = RuleDocs.All.Select(d => d.Id).Where(id => id.StartsWith('R'))
            .Concat(PullRequestCheckDocs.All.Select(d => d.Id)).ToList();
        Assert.Equal(16, live.Count(id => id.StartsWith('R')));

        var failures = new List<string>();
        foreach (var id in live)
        {
            var status = (await site.Api.GetAsync($"/Rules/{id}")).Status;
            if (status != 200)
            {
                failures.Add($"{id}: {status}");
            }
        }

        foreach (var id in PullRequestCheckDocs.Reserved)
        {
            var status = (await site.Api.GetAsync($"/Rules/{id}")).Status;
            if (status != 404)
            {
                failures.Add($"{id}: {status} (expected 404)");
            }
        }

        Assert.True(failures.Count == 0, string.Join(", ", failures));
    }

    /// <summary>A-01.</summary>
    [SmokeFact]
    public async Task Api_OneR002Finding()
    {
        var response = await site.Api.PostAsync("/api/lint", new() { DataObject = new { content = "Handle errors properly." } });

        Assert.Equal(200, response.Status);
        using var json = JsonDocument.Parse(await response.TextAsync());
        var findings = json.RootElement.GetProperty("findings").EnumerateArray().ToList();
        Assert.Equal("R002", Assert.Single(findings).GetProperty("rule").GetString());
    }
}

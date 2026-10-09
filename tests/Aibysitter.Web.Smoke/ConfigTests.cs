using Microsoft.Playwright;

namespace Aibysitter.Web.Smoke;

[Collection(SmokeCollection.Name)]
[Trait("Category", "Smoke")]
public class ConfigTests(SmokeFixture site)
{
    /// <summary>GC-04.</summary>
    [SmokeFact]
    public async Task Download_MatchesShownJson()
    {
        await using var s = await site.SessionAsync();
        await s.Page.GotoAsync("/GitHub/Config");
        await s.Page.ClickAsync("#config-form button[type=submit]:not(.secondary)");
        var shown = await s.Page.Locator("#config-json").InnerTextAsync();

        var download = await s.Page.RunAndWaitForDownloadAsync(() => s.Page.ClickAsync("#config-form button.secondary"));
        var saved = await File.ReadAllTextAsync((await download.PathAsync())!);

        Assert.Equal("aibysitter.json", download.SuggestedFilename);
        Assert.Contains("\"conclusion\"", shown);
        Assert.Equal(shown.ReplaceLineEndings("\n").TrimEnd(), saved.ReplaceLineEndings("\n").TrimEnd());
    }
}

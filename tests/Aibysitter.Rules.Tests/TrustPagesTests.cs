using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Aibysitter.Rules.Tests;

public class TrustPagesTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Theory]
    [InlineData("/Privacy", "The text is never stored or logged. Request logs record IP address, path and status for 14 days.")]
    [InlineData("/Privacy", "Logs record repository, pull request number, short commit SHA, delivery and check run IDs, finding count and any error text for 14 days. No file contents are logged.")]
    [InlineData("/Privacy", "Holds a queue file for the duration of a review: installation ID, repository, pull request number, head and base commits, check run ID and delivery ID.")]
    [InlineData("/Privacy", "Reads, at the pull request's head commit: the changed files and their diff, and rules files; and <code>.github/aibysitter.json</code> from the base commit.")]
    [InlineData("/Privacy", "and as one comment when the config sets <code>comment</code>.")]
    [InlineData("/Privacy", "<td>Reading files at the head commit and the config at the base commit</td>")]
    [InlineData("/Privacy", "Holds a queue file for the duration of a review")]
    [InlineData("/Privacy", "<td>Checks</td>")]
    [InlineData("/Privacy", "Lint by URL and badges: the server fetches the file from raw.githubusercontent.com, so GitHub sees the request come from this site. For public files it finds, it stores the repository name, file name, score, grade, ruleset version and time, kept up to 400 days, and shows them as score history. Private repositories cannot be read, so nothing is stored for them. The repository name is not logged.")]
    [InlineData("/Privacy", "Installed GitHub Apps")]
    [InlineData("/Privacy", "Share links put the text, format and rule choices in the URL after #. Browsers do not send that part to the server. Anyone with the link can read the text.")]
    [InlineData("/About", "Jon Bailey")]
    [InlineData("/About", "href=\"https://github.com/jbailey2900/aibysitter\"")]
    [InlineData("/About", "Code: MIT. Gallery content: CC0.")]
    [InlineData("/About", "href=\"https://github.com/jbailey2900\"")]
    [InlineData("/GitHub", "<h2>Security model</h2>")]
    [InlineData("/GitHub", "href=\"/Privacy\"")]
    [InlineData("/GitHub", "To uninstall:")]
    [InlineData("/GitHub", ">Install the GitHub App</a>")]
    public async Task PageContent(string path, string expected)
    {
        Assert.Contains(expected, await factory.CreateClient().GetStringAsync(path));
    }

    [Fact]
    public async Task Privacy_HasApiLine()
    {
        var html = await factory.CreateClient().GetStringAsync("/Privacy");

        Assert.Contains("<h2>API</h2>", html);
        Assert.Contains("The text is not stored or logged. Request logs record IP address, path and status for 14 days.", html);
    }

    [Fact]
    public async Task Footer_LinksAboutPrivacyNotes()
    {
        var html = await factory.CreateClient().GetStringAsync("/");

        Assert.Contains(@"<a href=""/About"">About</a>", html);
        Assert.Contains(@"<a href=""/Privacy"">Privacy</a>", html);
        Assert.Contains(@"<a href=""/Notes"">Notes</a>", html);
    }

    [Theory]
    [InlineData("/", 0)]
    [InlineData("/Lint", 1)]
    public async Task OnlyCookie_IsTheAntiforgeryToken(string path, int expected)
    {
        var response = await factory.CreateClient().GetAsync(path);
        var cookies = response.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToList() : [];

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, cookies.Count);
        Assert.All(cookies, c => Assert.StartsWith(".AspNetCore.Antiforgery.", c, StringComparison.Ordinal));
    }
}

using System.Net;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Aibysitter.Rules.PullRequests;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Aibysitter.Rules.Tests;

public partial class ConfigPageTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly string[] AllIds = [.. PullRequestCheckDocs.All.Select(d => d.Id), .. RuleDocs.All.Select(d => d.Id)];

    private async Task<HttpResponseMessage> Post(IEnumerable<string> enabled, string? scope = null, string conclusion = "Advisory", string? path = null, string? handler = null, bool comment = false, string? ignore = null)
    {
        var client = factory.CreateClient();
        var form = await client.GetStringAsync("/GitHub/Config");
        var fields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", TokenRegex().Match(form).Groups[1].Value),
            new("Posted", "true"),
            new("Conclusion", conclusion),
        };
        fields.AddRange(enabled.Select(id => KeyValuePair.Create("Enabled", id)));
        if (scope is not null)
        {
            fields.Add(new("Scope", scope));
        }

        if (path is not null)
        {
            fields.Add(new("TestPath", path));
        }

        if (comment)
        {
            fields.Add(new("Comment", "true"));
        }

        if (ignore is not null)
        {
            fields.Add(new("Ignore", ignore));
        }

        using var content = new FormUrlEncodedContent(fields);
        return await client.PostAsync("/GitHub/Config" + (handler is null ? "" : "?handler=" + handler), content);
    }

    private async Task<string> PostHtml(IEnumerable<string> enabled, string? scope = null, string conclusion = "Advisory", string? path = null)
    {
        var response = await Post(enabled, scope, conclusion, path);
        var html = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, LintClient.Snippet(html));
        return html;
    }

    private static string Encoded(string json) => HtmlEncoder.Default.Encode(json);

    [Fact]
    public async Task Get_SaysConfigIsReadFromTheBaseBranch() =>
        Assert.Contains("The App reads this file from the base branch: a change takes effect on pull requests reviewed after it merges.", await factory.CreateClient().GetStringAsync("/GitHub/Config"));

    [Fact]
    public async Task Get_ListsEveryCheckAndRule_AllChecked_NoOutput()
    {
        var html = await factory.CreateClient().GetStringAsync("/GitHub/Config");

        foreach (var id in AllIds)
        {
            Assert.Contains($"<input type=\"checkbox\" name=\"Enabled\" value=\"{id}\" checked=\"checked\" />", html);
        }

        Assert.Contains("<input type=\"radio\" name=\"Conclusion\" value=\"Advisory\" checked=\"checked\" />", html);
        Assert.Contains("formaction=\"/GitHub/Config?handler=Download\"", html);
        Assert.DoesNotContain("id=\"config-json\"", html);
    }

    [Fact]
    public async Task GitHubPage_LinksToGenerator()
    {
        Assert.Contains("<a href=\"/GitHub/Config\">Generate a config file</a>", await factory.CreateClient().GetStringAsync("/GitHub"));
    }

    [Fact]
    public async Task Post_AllEnabled_NoScope_WritesConclusionOnly()
    {
        var html = await PostHtml(AllIds);

        Assert.Contains("<pre id=\"config-json\"><code>" + Encoded("{\n  \"conclusion\": \"advisory\"\n}\n") + "</code></pre>", html);
        Assert.DoesNotContain("<h2>Errors</h2>", html);
        Assert.Contains("data-copy=\"config-json\"", html);
    }

    [Fact]
    public async Task Post_UncheckedIds_ScopeAndFailOnErrors_WritesAllKeys_KeepsFormState()
    {
        var html = await PostHtml(AllIds.Except(["P002", "R004"]), "src/**\r\ntests/**\r\n\r\n", "FailOnErrors");

        var expected = "{\n  \"scope\": [\n    \"src/**\",\n    \"tests/**\"\n  ],\n  \"conclusion\": \"fail-on-errors\",\n  \"disable\": [\n    \"P002\",\n    \"R004\"\n  ]\n}\n";
        Assert.Contains(Encoded(expected), html);
        Assert.Contains("<input type=\"checkbox\" name=\"Enabled\" value=\"P002\" />", html);
        Assert.Contains("<input type=\"radio\" name=\"Conclusion\" value=\"FailOnErrors\" checked=\"checked\" />", html);
        Assert.Contains("Scope does not include <code>.github/aibysitter.json</code>", html);
    }

    [Fact]
    public async Task Post_FailOnWarnings_WritesValue_KeepsRadio()
    {
        var html = await PostHtml(AllIds, "", "FailOnWarnings");

        Assert.Contains("<pre id=\"config-json\"><code>" + Encoded("{\n  \"conclusion\": \"fail-on-warnings\"\n}\n") + "</code></pre>", html);
        Assert.Contains("<input type=\"radio\" name=\"Conclusion\" value=\"FailOnWarnings\" checked=\"checked\" />", html);
    }

    [Fact]
    public async Task Post_Comment_WritesKey_KeepsCheckbox()
    {
        var response = await Post(AllIds, comment: true);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("<pre id=\"config-json\"><code>" + Encoded("{\n  \"conclusion\": \"advisory\",\n  \"comment\": true\n}\n") + "</code></pre>", html);
        Assert.Contains("name=\"Comment\" value=\"true\" checked=\"checked\"", html);
    }

    [Fact]
    public async Task Post_Ignore_WritesEntries_ParsesClean()
    {
        var response = await Post(AllIds, ignore: "docs/**\n\ntests/fixtures/**\ndocs/**");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("&quot;ignore&quot;: [", html, StringComparison.Ordinal);
        Assert.Contains("&quot;tests/fixtures/**&quot;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<h2>Errors</h2>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Post_RejectedGlob_ShowsErrorWithLine()
    {
        var html = await PostHtml(AllIds, "src/**.cs");

        Assert.Contains("<li>Line 3: .github/aibysitter.json: scope entry &quot;src/**.cs&quot;: ** must be a whole path segment", html);
    }

    [Fact]
    public async Task Post_NothingChecked_DisablesEverything()
    {
        var html = await PostHtml([]);

        foreach (var id in AllIds)
        {
            Assert.Contains($"    &quot;{id}&quot;", html);
        }
    }

    [Fact]
    public async Task Post_UnknownEnabledIds_AreIgnored()
    {
        var html = await PostHtml([.. AllIds, "X999", "<b>"]);

        Assert.DoesNotContain("disable", Regex.Match(html, "<pre id=\"config-json\">.*?</pre>", RegexOptions.Singleline).Value);
    }

    [Theory]
    [InlineData("src/**", "src/Orders/OrderService.cs", "<code>src/Orders/OrderService.cs</code> is in scope (matches <code>src/**</code>).")]
    [InlineData("src/**", "docs/notes.txt", "<code>docs/notes.txt</code> is outside scope; P004 flags it.")]
    [InlineData("src/**\n.github/**", ".github/aibysitter.json", "is in scope (matches <code>.github/**</code>).")]
    [InlineData("Src/**", "src/a.cs", "is outside scope")]
    [InlineData("", "src/a.cs", "<code>src/a.cs</code>: no scope set; P004 is off.")]
    public async Task TestPath_UsesAppGlob(string scope, string path, string expected)
    {
        var html = await PostHtml(AllIds, scope, path: path);

        Assert.Contains(expected, html);
    }

    [Fact]
    public async Task ConfigFileInScope_NoWarning()
    {
        Assert.DoesNotContain("Scope does not include", await PostHtml(AllIds, "**"));
    }

    [Fact]
    public async Task Download_ReturnsFile()
    {
        var response = await Post(AllIds.Except(["P013"]), "src/**", handler: "Download");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("aibysitter.json", response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("{\n  \"scope\": [\n    \"src/**\"\n  ],\n  \"conclusion\": \"advisory\",\n  \"disable\": [\n    \"P013\"\n  ]\n}\n", body);
        Assert.Empty(RepoConfig.Parse(body).Errors);
    }

    [Fact]
    public async Task OverLongScope_ShowsError_NoOutput_NoDownload()
    {
        var scope = new string('a', 10_001);
        var html = await PostHtml(AllIds, scope);
        var download = await Post(AllIds, scope, handler: "Download");

        Assert.Contains("Scope is limited to 10,000 characters.", html);
        Assert.DoesNotContain("id=\"config-json\"", html);
        Assert.Equal("text/html", download.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Post_WithoutToken_IsRejected()
    {
        using var content = new FormUrlEncodedContent([KeyValuePair.Create("Posted", "true")]);
        var response = await factory.CreateClient().PostAsync("/GitHub/Config", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [GeneratedRegex(@"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""")]
    private static partial Regex TokenRegex();
}

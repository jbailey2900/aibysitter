using Microsoft.Playwright;

namespace Aibysitter.Web.Smoke;

[Collection(SmokeCollection.Name)]
[Trait("Category", "Smoke")]
public class LintTests(SmokeFixture site)
{
    private static ILocator Results(IPage page) => page.Locator("#lint-results");

    /// <summary>Fills the textarea and clicks Lint; waits for the findings heading.</summary>
    internal static async Task LintAsync(IPage page, string text)
    {
        await page.GotoAsync("/Lint");
        await page.FillAsync("#RulesText", text);
        await page.ClickAsync("#lint-form button[type=submit]");
        await page.Locator("#lint-results h2", new() { HasTextRegex = new("^Findings \\(") }).WaitForAsync();
    }

    /// <summary>L-10.</summary>
    [SmokeFact]
    public async Task Sample_SixFindings_76C()
    {
        await using var s = await site.SessionAsync();

        await s.Page.GotoAsync("/Lint?sample=true");

        Assert.Equal("Findings (6)", await Results(s.Page).Locator("h2", new() { HasTextRegex = new("^Findings") }).InnerTextAsync());
        Assert.Equal("76 / 100 — C", await Results(s.Page).Locator(".score-value").InnerTextAsync());
    }

    /// <summary>L-08, L-09.</summary>
    [SmokeFact]
    public async Task BrowserLint_NoteShown_NoPost()
    {
        await using var s = await site.SessionAsync();
        var posts = new List<string>();
        s.Page.Request += (_, r) =>
        {
            if (r.Method == "POST")
            {
                posts.Add(r.Url);
            }
        };

        await LintAsync(s.Page, "# Rules\n- Handle errors properly.\n");

        await Assertions.Expect(Results(s.Page)).ToContainTextAsync("Linted in your browser. The text was not sent.");
        Assert.Empty(posts);
    }

    private static ILocator FindingRows(IPage page) => Results(page).Locator("table.findings").First.Locator("tbody tr");

    /// <summary>L-23.</summary>
    [SmokeFact]
    public async Task FileWideSuppression_Suppressed1_100A()
    {
        await using var s = await site.SessionAsync();

        await LintAsync(s.Page, "<!-- aibysitter-disable R002 -->\n# Rules\n- Handle errors properly.\n");

        await Assertions.Expect(Results(s.Page).Locator("h2", new() { HasText = "Suppressed (1)" })).ToBeVisibleAsync();
        Assert.Equal("100 / 100 — A", await Results(s.Page).Locator(".score-value").InnerTextAsync());
        Assert.Equal("Findings (0)", await Results(s.Page).Locator("h2", new() { HasTextRegex = new("^Findings") }).InnerTextAsync());
    }

    /// <summary>L-28.</summary>
    [SmokeFact]
    public async Task R002Off_NoR002Finding_RulesOffNote()
    {
        await using var s = await site.SessionAsync();
        await s.Page.GotoAsync("/Lint");
        await s.Page.ClickAsync("details.rules-picker summary");
        await s.Page.UncheckAsync("input[name=Enabled][value=R002]");

        await s.Page.FillAsync("#RulesText", "# Rules\n- Handle errors properly.\n");
        await s.Page.ClickAsync("#lint-form button[type=submit]");

        await Assertions.Expect(Results(s.Page)).ToContainTextAsync("Rules off: R002.");
        await Assertions.Expect(Results(s.Page).Locator("a[href='/Rules/R002']")).ToHaveCountAsync(0);
    }

    /// <summary>L-31.</summary>
    [SmokeFact]
    public async Task Lines201_R004WarningAtLine201()
    {
        await using var s = await site.SessionAsync();

        await LintAsync(s.Page, string.Join("\n", Enumerable.Range(1, 201).Select(i => $"- Use tabs in file {i}.")));

        var row = FindingRows(s.Page).Filter(new() { Has = s.Page.Locator("a[href='/Rules/R004']") });
        await Assertions.Expect(row).ToHaveCountAsync(1);
        var cells = await row.Locator("td").AllInnerTextsAsync();
        Assert.Equal("201", cells[0]);
        Assert.Equal("Warning", cells[2]);
    }

    /// <summary>L-33.</summary>
    [SmokeFact]
    public async Task Empty_PasteMessage()
    {
        await using var s = await site.SessionAsync();
        await s.Page.GotoAsync("/Lint");

        await s.Page.ClickAsync("#lint-form button[type=submit]");

        await Assertions.Expect(s.Page.Locator("#lint-form .error")).ToHaveTextAsync("Paste a rules file to lint.");
    }

    /// <summary>L-35. The textarea's maxlength stops a paste at 100,000; it is removed to send 100,001.</summary>
    [SmokeFact]
    public async Task Over100000_LimitMessage()
    {
        await using var s = await site.SessionAsync();
        await s.Page.GotoAsync("/Lint");
        await s.Page.EvalOnSelectorAsync("#RulesText", "e => e.removeAttribute('maxlength')");

        await s.Page.FillAsync("#RulesText", new string('x', 100_001));
        await s.Page.ClickAsync("#lint-form button[type=submit]");

        await Assertions.Expect(s.Page.Locator("#lint-form .error")).ToHaveTextAsync("Input is limited to 100,000 characters.");
    }

    /// <summary>L-45.</summary>
    [SmokeFact]
    public async Task ShareLink_RestoresTextFormatAndDisabledRules()
    {
        const string text = "# Rules\n- Handle errors properly.\n- Use tabs.\n";
        await using var s = await site.SessionAsync();
        await s.Page.GotoAsync("/Lint");
        await s.Page.SelectOptionAsync("#Format", "AgentsMd");
        await s.Page.ClickAsync("details.rules-picker summary");
        await s.Page.UncheckAsync("input[name=Enabled][value=R002]");
        await s.Page.FillAsync("#RulesText", text);
        await s.Page.ClickAsync("#lint-form button[type=submit]");
        await s.Page.ClickAsync("section.share button");
        var url = await s.Page.InputValueAsync("#share-url");
        Assert.Contains("#s=", url);

        var opened = await s.Context.NewPageAsync();
        await opened.GotoAsync(url);

        await Assertions.Expect(opened.Locator("#RulesText")).ToHaveValueAsync(text);
        await Assertions.Expect(opened.Locator("#Format")).ToHaveValueAsync("AgentsMd");
        await Assertions.Expect(opened.Locator("input[name=Enabled][value=R002]")).Not.ToBeCheckedAsync();
        await Assertions.Expect(opened.Locator("input[name=Enabled][value=R003]")).ToBeCheckedAsync();
    }

    /// <summary>L-56.</summary>
    [SmokeFact]
    public async Task NotARepo_InvalidInputMessage()
    {
        await using var s = await site.SessionAsync();
        await s.Page.GotoAsync("/Lint");

        await s.Page.FillAsync("#repo", "not a repo");
        await s.Page.ClickAsync("#url-form button[type=submit]");

        await Assertions.Expect(Results(s.Page).Locator("p.error")).ToHaveTextAsync("Enter a GitHub repository as owner/repo or its URL.");
    }

    /// <summary>GA-06.</summary>
    [SmokeFact]
    public async Task GalleryPrefill_TextAndFormat()
    {
        await using var s = await site.SessionAsync();
        var file = await site.Api.GetAsync("/gallery/minimal-starter/CLAUDE.md");
        Assert.Equal(200, file.Status);

        await s.Page.GotoAsync("/Lint?gallery=minimal-starter");

        Assert.Equal((await file.TextAsync()).ReplaceLineEndings("\n"), (await s.Page.InputValueAsync("#RulesText")).ReplaceLineEndings("\n"));
        await Assertions.Expect(s.Page.Locator("#Format")).ToHaveValueAsync("ClaudeMd");
    }
}

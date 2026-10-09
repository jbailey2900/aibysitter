using Aibysitter.Web.Gallery;

namespace Aibysitter.Rules.Tests.Parity;

internal sealed record ParityInput(string Name, string Text, RulesFormat Format, IReadOnlyList<string>? Disable = null);

/// <summary>Inputs both engines must agree on: gallery entries, fixtures, rule note examples, and edge cases.</summary>
internal static class ParityInputs
{
    public static IReadOnlyList<ParityInput> All { get; } = [.. Gallery(), .. Fixtures(), .. RuleDocExamples(), .. EdgeCases(), .. Disabled()];

    private static IEnumerable<ParityInput> Disabled()
    {
        var sample = Web.Samples.SampleRules.Text;
        yield return new ParityInput("disable/one", sample, RulesFormat.Auto, ["R001"]);
        yield return new ParityInput("disable/several", sample, RulesFormat.Auto, ["R002", "R003", "R005"]);
        yield return new ParityInput("disable/all-findings", sample, RulesFormat.Auto, ["R001", "R002", "R003", "R005"]);
        yield return new ParityInput("disable/suppressed-rule", "# T\n<!-- aibysitter-disable R001 -->\n## S\n- Use tabs because x.\n- Handle errors properly.", RulesFormat.Auto, ["R001"]);
        yield return new ParityInput("format/cursorrules-comment-block", "# Project\n# Language: TypeScript\n\nlanguage: x\n\n# Stack\n\n## Empty\n## Style\n- y\n", RulesFormat.CursorRules);
        yield return new ParityInput("format/windsurfrules-comment-block", "# Rules for Windsurf\n\n# Updates: refresh\n# through your host.\n\nproject: x\n", RulesFormat.WindsurfRules);
        yield return new ParityInput("disable/mdc", "---\nalwaysApply: maybe\n---\n# Rules\n- Use tabs.", RulesFormat.CursorMdc, ["R016"]);
    }

    private static IEnumerable<ParityInput> Gallery() =>
        new GalleryCatalog(new LintEngine()).All.Select(e => new ParityInput("gallery/" + e.Id, e.Content, e.Format));

    private static IEnumerable<ParityInput> Fixtures()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures");
        return Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(path => new ParityInput("fixtures/" + Path.GetRelativePath(root, path).Replace('\\', '/'), File.ReadAllText(path), RulesFormat.Auto));
    }

    private static IEnumerable<ParityInput> RuleDocExamples() =>
        RuleDocs.All.Where(d => !d.AppOnly).SelectMany(d => new[]
        {
            new ParityInput($"ruledocs/{d.Id}/bad", d.BadExample, RulesFormat.Auto),
            new ParityInput($"ruledocs/{d.Id}/good", d.GoodExample, RulesFormat.Auto),
        });

    private static IEnumerable<ParityInput> EdgeCases()
    {
        yield return Edge("crlf", "# Title\r\n\r\n## Build\r\n- Always use tabs.\r\n- Never use tabs.\r\n- Handle errors properly.\r");
        yield return Edge("unicode-space", "# T\n\n## S\n- Always\u00A0use tabs.\n- Never use\u2028tabs.\n- Try\u3000to keep it short.");
        yield return Edge("zwj-boundary", "# T\n\n## S\n- Do it because\u200Dthe build.\n- Do it\u200Cbecause the build.\n- Do it because the build.");
        yield return Edge("case-folding", "# T\n\n## S\n- Always use \u0130stanbul time.\n- never use i\u0307stanbul time.\n- ALWAYS \u03A3IGMA mode.\n- Never \u03C3igma mode.");
        yield return Edge("suppressions", "# T\n<!-- aibysitter-disable r001 -->\n## S\n- Use tabs because reasons.\n<!-- aibysitter-disable-next-line R002, R007 -->\n- Handle errors properly, if possible.\n- Handle errors properly, if possible.");
        yield return Edge("fences", "# T\n\n## S\n~~~~\ncode\n~~~\nstill code\n~~~~~ \n- Use tabs because x.\n```js\nunclosed\n\n");
        yield return Edge("fence-one-line", "# T\n\n## S\n- Run it.\n```\nlast");
        yield return Edge("emphasis", "# T\n\n## S\n- IMPORTANT: a.\n- CRITICAL: b.\n- You MUST c.\n- NEVER d.\n- Do e!!\n- `MUST` in code.");
        yield return Edge("paragraph", "# T\n\n## S\n" + string.Join(" ", Enumerable.Repeat("Word and more words here.", 20)) + "\n" + string.Join(" ", Enumerable.Repeat("x", 5)) + "\n\n- item " + string.Join(" ", Enumerable.Repeat("y", 90)));
        yield return Edge("persona-hedge-xref", "# T\n\nYou are a senior staff engineer.\n\n## S\n- Ideally, keep it small.\n- Consider whether to split.\n- Please consider caching.\n- As mentioned above, run tests.\n- See \"Build\" above.\n- Ensure the build passes.\n- Ensure quality.");
        yield return Edge("empty-sections", "# T\n## A\n## B\n<!-- note -->\n### C\ntext\n## D #\n");
        yield return Edge("duplicates", "# T\n## A\n### Notes here for you\n- Run tests before commit.\n* run  tests before commit.\n## B\n### Notes here for you\n---\n| a | b | c | d |\n| a | b | c | d |");
        yield return Edge("secrets", SecretsText());
        yield return Edge("long-file", string.Join("\n", Enumerable.Range(1, 230).Select(i => $"- Line {i} says something.")));
        yield return Edge("mdc-ok", "---\ndescription: Storefront rules\nglobs: [\"src/**/*.ts\", 'app/**']\nalwaysApply: false\n---\n# Rules\n- Use tabs.");
        yield return Edge("mdc-bad", "---\ndescriptoin: x\nalwaysApply: maybe\nglobs:\n  - \"a/**\"\n  - b\n---\n# Rules\n- Use tabs.", RulesFormat.CursorMdc);
        yield return Edge("mdc-never", "---\nalwaysApply: false\nglobs:\n...\n# Rules", RulesFormat.CursorMdc);
        yield return Edge("mdc-no-frontmatter", "# Rules\n- Use tabs.", RulesFormat.CursorMdc);
        yield return Edge("frontmatter-unclosed", "---\ndescription: x\n# Rules\n- Use tabs because x.");
        yield return Edge("frontmatter-markdown", "---\ntitle: x\n---\n# Rules\n- Use tabs.", RulesFormat.ClaudeMd);
        yield return Edge("labels-links-paths", "# T\n\n## S\n- Errors: handle them.\n- Handle [the docs](https://x.y/z) properly.\n- Handle src/app.ts properly.\n- Optimize for speed.\n- Clean up README.md.\n- Deal with it — handle it, then manage it; ensure it.");
        yield return Edge("bom-and-nel", "# T\n## A\n\uFEFF\n## B\n\u0085\n## C\n- Run it.\u0085\n");
        yield return Edge("empty", "");
        yield return Edge("only-newlines", "\n\n\n");
        yield return Edge("headings", "# a #\n## C##\n#\n######\n#foo\n#  \n### x # #\n####### seven\n   # three spaces\n    # four spaces\n## End ##  \n- Run it.");
        foreach (var redos in SecurityInputs.ParityLines)
        {
            yield return Edge("redos/" + redos.Name, "# T\n\n## S\n" + redos.Text);
        }
    }

    private static ParityInput Edge(string name, string text, RulesFormat format = RulesFormat.Auto) => new("edge/" + name, text, format);

    // Built at runtime so no secret-shaped literal sits in the repository.
    private static string SecretsText() => string.Join("\n",
        "# T",
        "- Key " + "AKIA" + new string('Q', 16) + " and " + "ghp_" + new string('a', 36),
        "- Conn: Server=x;Password=" + "hunter2x" + ";",
        "- Url postgres://u:" + "s3cretpw" + "@db.internal/x and http://u:" + "abcdefgh" + "@localhost/",
        "- Placeholder Password=your-password-here and " + "sk-" + "proj-" + new string('B', 24),
        "-----BEGIN " + "RSA PRIVATE KEY-----",
        "- Token " + "eyJ" + new string('c', 12) + ".eyJ" + new string('d', 12) + "." + new string('e', 12));
}

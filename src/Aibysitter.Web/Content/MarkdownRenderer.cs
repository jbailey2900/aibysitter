using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Aibysitter.Web.Content;

/// <summary>
/// Markdown to HTML for notes and gallery entries. Raw HTML is escaped, YAML frontmatter is dropped, and the output
/// carries no inline styles or scripts, so it renders under the site's CSP. Links and images keep only http, https,
/// mailto and relative targets; others render as their text.
/// </summary>
public static partial class MarkdownRenderer
{
    private const int MaxHeadingLevel = 6;

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UseYamlFrontMatter()
        .UsePipeTables()
        .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)
        .Build();

    /// <param name="headingOffset">Levels added to every heading, so embedded content sits under the page's own headings.</param>
    public static string ToHtml(string markdown, int headingOffset = 0)
    {
        var document = Markdown.Parse(markdown, Pipeline);
        foreach (var heading in document.Descendants<HeadingBlock>())
        {
            heading.Level = Math.Min(MaxHeadingLevel, heading.Level + headingOffset);
        }

        // Column alignment renders as an inline style attribute, which the CSP blocks.
        foreach (var column in document.Descendants<Table>().SelectMany(t => t.ColumnDefinitions))
        {
            column.Alignment = null;
        }

        DropUnsafeLinks(document);
        return document.ToHtml(Pipeline);
    }

    private static readonly HashSet<string> AllowedSchemes = new(StringComparer.OrdinalIgnoreCase) { "http", "https", "mailto" };

    /// <summary>No scheme (relative, <c>/</c>, <c>#</c>) or an allowed one. Whitespace and control characters are ignored, as browsers do.</summary>
    internal static bool IsAllowedUrl(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return true;
        }

        var match = SchemeRegex().Match(string.Concat(url.Where(c => c > ' ')));
        return !match.Success || AllowedSchemes.Contains(match.Groups[1].Value);
    }

    private static void DropUnsafeLinks(MarkdownDocument document)
    {
        // Collected before any change: moving inlines during the walk can end it early.
        var unsafeLinks = document.Descendants<Inline>()
            .Where(i => i is LinkInline link ? !IsAllowedUrl(link.Url) : i is AutolinkInline { IsEmail: false } autolink && !IsAllowedUrl(autolink.Url))
            .ToList();

        foreach (var inline in unsafeLinks)
        {
            switch (inline)
            {
                case LinkInline { IsImage: true } image:
                    image.InsertBefore(new LiteralInline(string.Concat(image.Descendants<LiteralInline>().Select(l => l.Content.ToString()))));
                    break;
                case LinkInline link:
                    while (link.FirstChild is { } child)
                    {
                        child.Remove();
                        link.InsertBefore(child);
                    }

                    break;
                case AutolinkInline autolink:
                    autolink.InsertBefore(new LiteralInline(autolink.Url));
                    break;
            }

            inline.Remove();
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex("^([A-Za-z][A-Za-z0-9+.-]*):")]
    private static partial System.Text.RegularExpressions.Regex SchemeRegex();
}

using Aibysitter.Rules;
using Aibysitter.Web.Content;
using Aibysitter.Web.Gallery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Aibysitter.Web.Pages.Gallery;

public class EntryModel(GalleryCatalog catalog, Infrastructure.SiteOptions site) : PageModel
{
    public GalleryEntry Entry { get; private set; } = null!;

    public string BadgeMarkdown { get; private set; } = string.Empty;

    public IReadOnlyDictionary<int, List<GalleryFinding>> FindingsByLine { get; private set; } = new Dictionary<int, List<GalleryFinding>>();

    public const string SourceView = "source";

    /// <summary>Levels added to the file's headings: the page already has h1 (entry name) and h2 (file name).</summary>
    private const int HeadingOffset = 2;

    /// <summary>True for the line-numbered source view; false for rendered Markdown.</summary>
    public bool IsSource { get; private set; }

    public string RenderedHtml { get; private set; } = string.Empty;

    /// <summary>Frontmatter lines, shown above the rendered body; empty when the file has none.</summary>
    public IReadOnlyList<string> FrontmatterLines { get; private set; } = [];

    public IActionResult OnGet(string id, string? view)
    {
        if (catalog.Find(id) is not { } entry || (view is not null && view != SourceView))
        {
            return NotFound();
        }

        Entry = entry;
        IsSource = view == SourceView;
        RenderedHtml = MarkdownRenderer.ToHtml(entry.Content, HeadingOffset);
        FrontmatterLines = entry.Lines.Take(RulesFile.Parse(entry.Content).Frontmatter?.EndLine ?? 0).ToList();
        BadgeMarkdown = $"[![Aibysitter lint score: {entry.Score.Grade} {entry.Score.Value}]({site.Url(entry.BadgePath)})]({site.Url($"/Gallery/{entry.Id}")})";
        FindingsByLine = entry.Findings.GroupBy(f => f.Finding.Line).ToDictionary(g => g.Key, g => g.ToList());
        return Page();
    }
}

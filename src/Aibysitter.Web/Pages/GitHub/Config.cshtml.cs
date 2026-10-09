using System.ComponentModel.DataAnnotations;
using System.Text;
using Aibysitter.Rules;
using Aibysitter.Rules.PullRequests;
using Aibysitter.Web.Config;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Aibysitter.Web.Pages.GitHub;

/// <summary>Builds <c>.github/aibysitter.json</c> from the form and validates it with the App's parser.</summary>
public class ConfigModel : PageModel
{
    public const string PatternTimeoutMessage = "A pattern took too long to match. Simplify the glob.";

    public const int MaxScopeLength = 10_000;
    public const int MaxPathLength = 1_000;
    public const string DownloadName = "aibysitter.json";

    /// <summary>Checked check and rule IDs. Unchecked IDs are written under <c>disable</c>.</summary>
    [BindProperty]
    public List<string> Enabled { get; set; } = [];

    /// <summary>Marks a posted form, so an empty <see cref="Enabled"/> means everything is off.</summary>
    [BindProperty]
    public bool Posted { get; set; }

    [BindProperty]
    [StringLength(MaxScopeLength, ErrorMessage = "Scope is limited to 10,000 characters.")]
    public string? Scope { get; set; }

    /// <summary>One glob per line, written as <c>ignore</c> entries that apply to every content check.</summary>
    [BindProperty]
    [StringLength(MaxScopeLength, ErrorMessage = "Ignore is limited to 10,000 characters.")]
    public string? Ignore { get; set; }

    [BindProperty]
    public ConclusionMode Conclusion { get; set; } = ConclusionMode.Advisory;

    [BindProperty]
    public bool Comment { get; set; }

    [BindProperty]
    [StringLength(MaxPathLength, ErrorMessage = "Path is limited to 1,000 characters.")]
    public string? TestPath { get; set; }

    public IReadOnlyList<RuleDoc> Checks => PullRequestCheckDocs.All;

    public IReadOnlyList<RuleDoc> Rules => RuleDocs.All;

    public bool IsEnabled(string id) => !Posted || Enabled.Contains(id, StringComparer.Ordinal);

    /// <summary>Generated file, set after a valid post.</summary>
    public string? Json { get; private set; }

    /// <summary>Errors from <see cref="RepoConfig.Parse"/> on the generated file.</summary>
    public IReadOnlyList<ConfigError> Errors { get; private set; } = [];

    /// <summary>Set when <see cref="TestPath"/> was given.</summary>
    public ScopeTest? PathTest { get; private set; }

    /// <summary>True when scope is set and does not include the config file, so editing it gets a P004 finding.</summary>
    public bool ConfigFileOutOfScope { get; private set; }

    public void OnGet()
    {
    }

    public IActionResult OnPost()
    {
        if (ModelState.IsValid)
        {
            Generate();
        }

        return Page();
    }

    public IActionResult OnPostDownload()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        Generate();
        return Errors.Count > 0 ? Page() : File(Encoding.UTF8.GetBytes(Json!), "application/json", DownloadName);
    }

    private void Generate()
    {
        var disable = Checks.Concat(Rules).Select(d => d.Id).Where(id => !IsEnabled(id)).ToList();
        Json = RepoConfigWriter.Write(RepoConfigWriter.ScopeLines(Scope), Conclusion, disable, Comment, RepoConfigWriter.ScopeLines(Ignore));
        var (config, errors) = RepoConfig.Parse(Json);
        Errors = errors;
        try
        {
            ConfigFileOutOfScope = config.HasScope && !config.InScope(RepoConfig.FilePath);
            if (TestPath?.Trim() is { Length: > 0 } path)
            {
                PathTest = new ScopeTest(path, config.HasScope, config.Scope.FirstOrDefault(g => g.IsMatch(path))?.Pattern);
            }
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            ModelState.AddModelError(nameof(TestPath), PatternTimeoutMessage);
        }
    }

    /// <param name="MatchedGlob">First scope glob matching <paramref name="Path"/>; null when none does.</param>
    public sealed record ScopeTest(string Path, bool HasScope, string? MatchedGlob);
}

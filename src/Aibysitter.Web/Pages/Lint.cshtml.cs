using System.ComponentModel.DataAnnotations;
using Aibysitter.Rules;
using Aibysitter.Web.Data;
using Aibysitter.Web.Infrastructure;
using Aibysitter.Web.Gallery;
using Aibysitter.Web.Linting;
using Aibysitter.Web.Samples;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace Aibysitter.Web.Pages;

public class LintModel(LintService lint, GalleryCatalog galleryCatalog, RawGitHubFetcher fetcher, IScoreHistory history, SiteOptions site) : PageModel
{
    public const int MaxContentLength = LintLimits.MaxContentLength;

    [BindProperty]
    [Required(ErrorMessage = "Paste a rules file to lint.")]
    [StringLength(MaxContentLength, ErrorMessage = "Input is limited to 100,000 characters.")]
    public string? RulesText { get; set; }

    [BindProperty]
    public RulesFormat Format { get; set; } = RulesFormat.Auto;

    /// <summary>Format the last lint ran as (Auto resolved).</summary>
    public RulesFormat? LintedFormat { get; private set; }

    public IEnumerable<SelectListItem> FormatOptions =>
        RulesFormats.Selectable.Select(f => new SelectListItem(RulesFormats.DisplayName(f), f.ToString(), f == Format));

    public IReadOnlyList<LintRow>? Results { get; private set; }

    public LintScore? Score { get; private set; }

    public IReadOnlyList<LintRow>? Suppressed { get; private set; }

    /// <summary>Checked rule IDs from the Rules section. Posted with <see cref="RulesPosted"/>; unchecked rules are disabled.</summary>
    [BindProperty]
    public List<string> Enabled { get; set; } = [];

    /// <summary>Marks that the Rules section was posted, so an empty <see cref="Enabled"/> means every rule is off.</summary>
    [BindProperty]
    public bool RulesPosted { get; set; }

    public IReadOnlyList<IRule> Rules => lint.Rules;

    /// <summary>Rules disabled for the last lint.</summary>
    public IReadOnlyList<string> Disabled { get; private set; } = [];

    public bool IsEnabled(string ruleId) => !Disabled.Contains(ruleId);

    /// <summary>Repository text from the URL form, echoed back.</summary>
    public string? RepoText { get; private set; }

    /// <summary>Set after a lint-by-URL request.</summary>
    public UrlLintResult? UrlLint { get; private set; }

    /// <summary>Recent scores for the repository and file just linted by URL, newest first; null when score history is off or nothing was found.</summary>
    public IReadOnlyList<ScorePoint>? History { get; private set; }

    public const int HistoryCount = 10;

    /// <summary>Badge URL for the file shown in <see cref="History"/>; <c>?file=</c> only when it is not the file the badge picks by default.</summary>
    public string? HistoryBadgeUrl { get; private set; }

    /// <summary>True when the page shows the built-in sample's results on load.</summary>
    public bool IsSample { get; private set; }

    /// <summary>Prefills the textarea from the sample (<c>?sample=true</c>) or a gallery entry (<c>?gallery=&lt;id&gt;</c>).</summary>
    public void OnGet(bool sample = false, string? gallery = null)
    {
        if (sample)
        {
            RulesText = SampleRules.Text;
            IsSample = true;
            Lint();
        }
        else if (gallery is not null && galleryCatalog.Find(gallery) is { } entry)
        {
            RulesText = entry.Content;
            Format = entry.Format;
        }
    }

    public IActionResult OnPost()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        Lint();
        return Page();
    }

    /// <summary>
    /// Lints a rules file from a public repository's default branch. Form fields <c>repo</c> and <c>file</c> (one of the
    /// supported names) are read here, not bound as handler arguments, which MVC logs at Debug level.
    /// </summary>
    public async Task<IActionResult> OnPostUrlAsync(CancellationToken cancellationToken)
    {
        ModelState.Clear();
        var repo = Request.Form["repo"].ToString();
        var file = Request.Form["file"].ToString();
        RepoText = repo;
        if (!RepoInput.TryParse(repo, out var parsed))
        {
            UrlLint = UrlLintResult.Invalid();
            return Page();
        }

        var fetched = await fetcher.FetchAsync(parsed!, file.Length == 0 ? null : file, cancellationToken);
        UrlLint = UrlLintResult.From(parsed!, fetched);
        if (fetched.Status == FetchStatus.Found)
        {
            RulesText = fetched.Content;
            Format = RulesFormats.FromFileName(fetched.FileName!) ?? RulesFormat.Auto;
            LintOutcome outcome;
            try
            {
                outcome = lint.Lint(RulesText!, Format, [], "url");
            }
            catch (LintTimeoutException ex)
            {
                UrlLint = UrlLint with { Error = ex.Message };
                return Page();
            }

            Show(outcome);
            await RecordAsync(ScoreHistoryKey.Repo(parsed!), fetched.FileName!, outcome.Score, cancellationToken);
            var isDefault = RawGitHubFetcher.FileNames.First(n => n == fetched.FileName || fetched.OtherFiles.Contains(n)) == fetched.FileName;
            HistoryBadgeUrl = site.Url(BadgeEndpoints.Path(parsed!, isDefault ? null : fetched.FileName));
        }

        return Page();
    }

    /// <summary>Only reached for a file fetched without credentials, so only public repositories are stored.</summary>
    private async Task RecordAsync(string repo, string fileName, LintScore score, CancellationToken cancellationToken)
    {
        if (!history.Enabled)
        {
            return;
        }

        await history.RecordAsync(new ScoreRecord(repo, fileName, RulesetVersion.Current, score.Value, score.Grade, ScoreSource.LintByUrl), cancellationToken);
        History = await history.RecentAsync(repo, fileName, HistoryCount, cancellationToken);
    }

    private void Show(LintOutcome outcome)
    {
        LintedFormat = outcome.Format;
        Results = outcome.Findings;
        Suppressed = outcome.Suppressed;
        Score = outcome.Score;
    }

    private void Lint()
    {
        var off = RulesPosted ? lint.Rules.Select(r => r.Id).Except(Enabled, StringComparer.OrdinalIgnoreCase) : Enumerable.Empty<string>();
        Disabled = lint.TryNormalizeDisabled(off, out var disabled, out _) ? disabled : [];
        try
        {
            Show(lint.Lint(RulesText!, Format, Disabled, "form"));
        }
        catch (LintTimeoutException ex)
        {
            ModelState.AddModelError(nameof(RulesText), ex.Message);
        }
    }
}

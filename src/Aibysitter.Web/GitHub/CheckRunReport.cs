using System.Globalization;
using System.Text;
using Aibysitter.Rules;
using Aibysitter.Rules.PullRequests;

namespace Aibysitter.Web.GitHub;

/// <param name="IsConfigError">An error in the repo config file; not a finding.</param>
public sealed record CheckRunAnnotation(string Path, int Line, Severity Severity, string Title, string Message, string RawDetails, bool IsConfigError = false);

public sealed record CheckRunReport(ReviewConclusion Conclusion, string Title, string Summary, IReadOnlyList<CheckRunAnnotation> Annotations)
{
    public const int AnnotationsPerRequest = 50;
    public const string ConfigErrorTitle = "Config error";
    public const string ConfigErrorDetails = "The default is used for this entry. Generator and key reference: https://aibysitting.net/GitHub/Config";

    public IEnumerable<IReadOnlyList<CheckRunAnnotation>> AnnotationBatches() => Annotations.Chunk(AnnotationsPerRequest);

    public static CheckRunReport Build(
        PullRequestReview review,
        IReadOnlyList<IPullRequestCheck> checks,
        IReadOnlyList<ChangedFile> files,
        RepoConfig config,
        IReadOnlyList<ConfigError> configErrors,
        IReadOnlyList<string>? notes = null,
        string? configNote = null)
    {
        ArgumentNullException.ThrowIfNull(review);

        var checkById = checks.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var removed = files.Where(f => f.Status == FileChangeStatus.Removed).Select(f => f.Path).ToHashSet(StringComparer.Ordinal);

        var annotations = review.Findings
            .Where(f => !removed.Contains(f.Path))
            .Select(f => new CheckRunAnnotation(f.Path, f.Line, f.SeverityOr(checkById[f.CheckId].Severity), $"{f.CheckId} {checkById[f.CheckId].Title}", f.Message, f.FixHint))
            .Concat(configErrors.Select(e => new CheckRunAnnotation(RepoConfig.FilePath, e.Line, Severity.Warning, ConfigErrorTitle, e.Message, ConfigErrorDetails, IsConfigError: true)))
            .ToList();

        var errors = review.Findings.Count(f => f.SeverityOr(checkById[f.CheckId].Severity) == Severity.Error);
        var warnings = review.Findings.Count(f => f.SeverityOr(checkById[f.CheckId].Severity) == Severity.Warning);
        var notices = review.Findings.Count(f => f.SeverityOr(checkById[f.CheckId].Severity) == Severity.Info);
        var title = review.Findings.Count switch
        {
            0 => "No findings",
            1 => "1 finding",
            var n => $"{n} findings",
        };

        if (review.Findings.Count > 0)
        {
            title += $" ({errors} error{(errors == 1 ? "" : "s")}, {warnings} warning{(warnings == 1 ? "" : "s")}"
                + (notices > 0 ? $", {notices} notice{(notices == 1 ? "" : "s")})" : ")");
        }

        if (configErrors.Count > 0)
        {
            title += $"; {configErrors.Count} config error{(configErrors.Count == 1 ? "" : "s")}";
        }

        var conclusion = config.FailsCheck && configErrors.Count > 0 ? ReviewConclusion.Failure : review.Conclusion;

        var summary = new StringBuilder();
        if (configNote is not null)
        {
            summary.AppendLine(configNote);
            summary.AppendLine();
        }

        summary.AppendLine($"Conclusion mode: `{RepoConfig.ConclusionName(config.Conclusion)}`. Scope: {(config.HasScope ? string.Join(", ", config.Scope.Select(g => $"`{g.Pattern}`")) : "not declared")}.");
        if (config.Ignore.Count > 0)
        {
            var ignored = files.Count(f => config.IsIgnoredByAny(f.Path));
            summary.AppendLine($"Ignored by config: {ignored} file{(ignored == 1 ? "" : "s")} ({string.Join("; ", config.Ignore.Select(Describe))}).");
        }

        summary.AppendLine();
        summary.AppendLine("| Check | Severity | Findings |");
        summary.AppendLine("|---|---|---|");
        foreach (var check in checks)
        {
            summary.AppendLine($"| {check.Id} {check.Title} | {(check is RulesFileLint ? "Per rule" : check.Severity.ToString())} | {(config.IsEnabled(check.Id) ? review.Findings.Count(f => f.CheckId == check.Id).ToString(CultureInfo.InvariantCulture) : "disabled")} |");
        }

        var disabledRules = config.Disabled.Where(id => id.StartsWith('R')).Order(StringComparer.Ordinal).ToList();
        if (disabledRules.Count > 0 && config.IsEnabled(RulesFileLint.CheckId))
        {
            summary.AppendLine();
            summary.AppendLine($"Rules disabled for P014: {string.Join(", ", disabledRules)}.");
        }

        var onRemoved = review.Findings.Where(f => removed.Contains(f.Path)).ToList();
        if (onRemoved.Count > 0)
        {
            summary.AppendLine();
            summary.AppendLine("Findings on removed files:");
            foreach (var f in onRemoved)
            {
                summary.AppendLine($"- `{f.Path}`: {f.CheckId} {f.Message}");
            }
        }

        if (notes is { Count: > 0 })
        {
            summary.AppendLine();
            foreach (var note in notes)
            {
                summary.AppendLine($"- {note}");
            }
        }

        if (configErrors.Count > 0)
        {
            summary.AppendLine();
            summary.AppendLine(config.FailsCheck
                ? $"Config errors (defaults used for these; the check fails under {RepoConfig.ConclusionName(config.Conclusion)}):"
                : "Config errors (defaults used for these):");
            foreach (var error in configErrors)
            {
                summary.AppendLine($"- Line {error.Line}: {error.Message}");
            }
        }

        return new CheckRunReport(conclusion, title, summary.ToString().TrimEnd(), annotations);
    }

    public const string TimeoutSummary = "Review stopped: a check exceeded the 1 second pattern limit on this pull request's content.";

    /// <summary>A pattern hit the regex match timeout. Neutral, like other review failures.</summary>
    public static CheckRunReport ForTimeout() => new(
        ReviewConclusion.Neutral,
        "Review failed",
        TimeoutSummary,
        []);

    public static CheckRunReport ForError(Exception ex) => new(
        ReviewConclusion.Neutral,
        "Review failed",
        $"Aibysitter could not complete this review ({ex.GetType().Name}). Push a new commit or redeliver the webhook to retry.",
        []);

    private static string Describe(IgnoreEntry entry) =>
        string.Join(", ", entry.Paths.Select(g => $"`{g.Pattern}`"))
        + (entry.Checks is null ? "" : $" for {string.Join(", ", entry.Checks.Order(StringComparer.Ordinal))}");
}

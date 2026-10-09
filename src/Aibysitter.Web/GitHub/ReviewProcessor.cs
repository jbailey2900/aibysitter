using Aibysitter.Rules;
using Aibysitter.Rules.PullRequests;
using Aibysitter.Rules.Repo;
using Aibysitter.Rules.Rules;

namespace Aibysitter.Web.GitHub;

public sealed class ReviewProcessor(IGitHubGateway gateway, PullRequestReviewer reviewer, ILogger<ReviewProcessor> logger, Stats.IUsageCounter? usage = null, TimeProvider? time = null)
{
    public const int MaxContentFetches = 100;

    /// <summary>Time limit for one review, GitHub calls included. Over it the check closes as "Review failed".</summary>
    public static readonly TimeSpan JobBudget = TimeSpan.FromSeconds(90);

    /// <summary>Unchanged rules files read for R006 when the PR removes or renames something.</summary>
    public const int MaxUnchangedRulesFiles = 10;

    /// <summary>package.json, Makefile, .gitignore, and MSBuild files read for R006.</summary>
    public const int MaxManifestFetches = 20;

    /// <returns>True when the check run was completed (with findings or as "Review failed"); false when it could not be closed.</returns>
    public async Task<bool> ProcessAsync(ReviewJob job, CancellationToken cancellationToken)
    {
        var pr = job.PullRequest;
        RepoConfig? readConfig = null;
        using var timeLimit = new CancellationTokenSource(JobBudget, time ?? TimeProvider.System);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeLimit.Token);
        var reviewToken = linked.Token;
        try
        {
            await MarkInProgressAsync(job, reviewToken);

            var files = await gateway.GetChangedFilesAsync(pr, reviewToken);
            var (config, configErrors, configNote) = await ReadConfigAsync(pr, files, reviewToken);
            readConfig = config;
            var budget = new ContentBudget();

            var notes = new List<string>();
            var tree = await TreeForRulesFilesAsync(pr, files, config, reviewToken);
            if (NeedsTreeForDeletedTests(files, config))
            {
                tree ??= await gateway.GetTreeAsync(pr, reviewToken);
            }

            var symlinks = files.Where(f => RulesFileLint.IsRulesFile(f) && tree?.Symlinks.Contains(f.Path) == true).ToList();
            notes.AddRange(symlinks.Select(SymlinkNote));

            // Rules files are fetched first so the shared cap never starves P014. Symlinked rules files are not fetched:
            // GitHub returns the target's content under the link's path.
            var toFetch = files.Where(NeedsHeadContent)
                .Where(f => !symlinks.Contains(f))
                .OrderBy(f => RulesFileLint.IsRulesFile(f) ? 0 : 1)
                .Take(MaxContentFetches)
                .Select(f => f.Path)
                .ToHashSet(StringComparer.Ordinal);

            var enriched = new List<ChangedFile>(files.Count);
            foreach (var file in files)
            {
                enriched.Add(toFetch.Contains(file.Path)
                    ? file with { HeadContent = await budget.ReadAsync(file.Path, () => gateway.GetFileContentAsync(pr, file.Path, reviewToken)) }
                    : file);
            }

            var (repo, unchanged) = await BuildRepoViewAsync(pr, files, enriched, config, tree, notes, budget, reviewToken);
            notes.AddRange(budget.Notes());

            var review = reviewer.Review(new PullRequestContext(enriched, config, repo, unchanged, tree?.Paths));
            var report = CheckRunReport.Build(review, reviewer.Checks, enriched, config, configErrors, notes, configNote);
            if (config.Comment)
            {
                var note = await new ReviewCommentPublisher(gateway, logger)
                    .PublishAsync(pr, ReviewComment.Build(report, pr, job.CheckRunId), review.Findings.Count > 0, reviewToken);
                if (note is not null)
                {
                    report = report with { Summary = $"{report.Summary}\n\n{note}" };
                }
            }

            await gateway.CompleteCheckRunAsync(pr, job.CheckRunId, report, reviewToken);
            usage?.Increment(Stats.UsageMetric.Review, report.Conclusion.ToString().ToLowerInvariant());

            logger.LogInformation(
                "Reviewed {PullRequest} (delivery {DeliveryId}): {FindingCount} findings, {Conclusion}",
                pr, job.DeliveryId, review.Findings.Count, review.Conclusion);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Review of {PullRequest} (delivery {DeliveryId}) failed", pr, job.DeliveryId);
            try
            {
                var conclusion = await ErrorConclusionAsync(pr, readConfig, cancellationToken);
                var failed = ex switch
                {
                    OperationCanceledException when timeLimit.IsCancellationRequested => CheckRunReport.ForBudget(conclusion),
                    System.Text.RegularExpressions.RegexMatchTimeoutException => CheckRunReport.ForTimeout(conclusion),
                    _ => CheckRunReport.ForError(ex, conclusion),
                };
                await gateway.CompleteCheckRunAsync(pr, job.CheckRunId, failed, cancellationToken);
                usage?.Increment(Stats.UsageMetric.Review, "error");
                return true;
            }
            catch (Exception closeEx) when (closeEx is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogError(closeEx, "Could not close check run {CheckRunId} for {PullRequest}", job.CheckRunId, pr);
                return false;
            }
        }
    }

    /// <summary>
    /// Under fail-on-warnings or fail-on-errors a review error fails the check, so an error cannot turn a failure into a pass;
    /// advisory stays neutral. When the config was not read before the error, one more read is tried; if that fails too, Failure.
    /// </summary>
    private async Task<ReviewConclusion> ErrorConclusionAsync(PullRequestRef pr, RepoConfig? config, CancellationToken cancellationToken)
    {
        if (config is null)
        {
            try
            {
                var content = pr.BaseSha is null
                    ? await gateway.GetFileContentAsync(pr, RepoConfig.FilePath, cancellationToken)
                    : await gateway.GetBaseFileContentAsync(pr, RepoConfig.FilePath, cancellationToken);
                config = ParseConfig(content).Config;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Config for {PullRequest} unreadable after a review error; closing as failure", pr);
                return ReviewConclusion.Failure;
            }
        }

        return config.FailsCheck ? ReviewConclusion.Failure : ReviewConclusion.Neutral;
    }

    /// <summary>Parses the config file; a file that was not decoded gives the defaults and one config error.</summary>
    internal static (RepoConfig Config, IReadOnlyList<ConfigError> Errors) ParseConfig(FileContent? content) =>
        content?.Skipped is { } reason
            ? (RepoConfig.Default, [new ConfigError(1, $"{RepoConfig.FilePath}: {reason}; defaults used")])
            : RepoConfig.Parse(content?.Text);

    public const string ConfigChangedNote =
        "This pull request changes `.github/aibysitter.json`. It was reviewed with the base branch's version; the change applies after merge.";

    /// <summary>
    /// Config from the base commit, so a pull request cannot change how it is itself reviewed. When the pull request
    /// changes the config file, its errors are those of the head version (the annotated file) and the summary says so.
    /// Jobs saved before the base commit was recorded read the head version.
    /// </summary>
    private async Task<(RepoConfig Config, IReadOnlyList<ConfigError> Errors, string? Note)> ReadConfigAsync(
        PullRequestRef pr, IReadOnlyList<ChangedFile> files, CancellationToken cancellationToken)
    {
        if (pr.BaseSha is null)
        {
            var (headConfig, headErrors) = ParseConfig(await gateway.GetFileContentAsync(pr, RepoConfig.FilePath, cancellationToken));
            return (headConfig, headErrors, null);
        }

        var (config, errors) = ParseConfig(await gateway.GetBaseFileContentAsync(pr, RepoConfig.FilePath, cancellationToken));
        if (!files.Any(f => f.Path == RepoConfig.FilePath || f.PreviousPath == RepoConfig.FilePath))
        {
            return (config, errors, null);
        }

        var headVersion = files.Any(f => f.Path == RepoConfig.FilePath && f.Status != FileChangeStatus.Removed)
            ? await gateway.GetFileContentAsync(pr, RepoConfig.FilePath, cancellationToken)
            : null;
        return (config, ParseConfig(headVersion).Errors, ConfigChangedNote);
    }

    /// <summary>The in_progress status is cosmetic; a failure is logged and the review continues.</summary>
    private async Task MarkInProgressAsync(ReviewJob job, CancellationToken cancellationToken)
    {
        try
        {
            await gateway.MarkInProgressAsync(job.PullRequest, job.CheckRunId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not mark check run {CheckRunId} in progress for {PullRequest}; reviewing anyway", job.CheckRunId, job.PullRequest);
        }
    }

    /// <summary>The head tree when P014 is enabled and the PR adds or changes a rules file; used to find symlinks.</summary>
    private async Task<RepoTree?> TreeForRulesFilesAsync(PullRequestRef pr, IReadOnlyList<ChangedFile> files, RepoConfig config, CancellationToken cancellationToken) =>
        config.IsEnabled(RulesFileLint.CheckId) && files.Any(RulesFileLint.IsRulesFile)
            ? await gateway.GetTreeAsync(pr, cancellationToken)
            : null;

    /// <summary>P008 reads the head file list when the pull request removes a test file.</summary>
    internal static bool NeedsTreeForDeletedTests(IReadOnlyList<ChangedFile> files, RepoConfig config) =>
        config.IsEnabled("P008") && files.Any(f => f.Status == FileChangeStatus.Removed && FileKinds.IsTestFile(f.Path));

    internal static string SymlinkNote(ChangedFile file) =>
        file.AddedLines.FirstOrDefault()?.Text.Trim() is { Length: > 0 } target
            ? $"P014 skipped {file.Path}: symlink to {target}."
            : $"P014 skipped {file.Path}: symlink.";

    /// <summary>
    /// File list and the few files R006 needs. Fetched only when P014 and R006 are enabled and the PR changes a rules
    /// file or removes / renames something.
    /// </summary>
    private async Task<(RepoSnapshot? Repo, IReadOnlyList<ChangedFile> Unchanged)> BuildRepoViewAsync(
        PullRequestRef pr,
        IReadOnlyList<ChangedFile> files,
        IReadOnlyList<ChangedFile> enriched,
        RepoConfig config,
        RepoTree? tree,
        List<string> notes,
        ContentBudget budget,
        CancellationToken cancellationToken)
    {
        if (!config.IsEnabled(RulesFileLint.CheckId) || !config.IsEnabled(MissingIdentifiers.RuleId))
        {
            return (null, []);
        }

        var changedRules = enriched.Where(f => RulesFileLint.IsRulesFile(f) && f.HeadContent is not null).ToList();
        var removed = RemovedIdentifiers.From(files);
        if (changedRules.Count == 0 && !removed.Any)
        {
            return (null, []);
        }

        tree ??= changedRules.Count == 0 ? await gateway.GetTreeAsync(pr, cancellationToken) : null;
        var paths = tree?.Paths;
        if (paths is null)
        {
            notes.Add("R006 skipped: the repository file list is too large for GitHub to return in full.");
            return (null, []);
        }

        var contents = changedRules.ToDictionary(f => f.Path, f => f.HeadContent, StringComparer.Ordinal);
        var changedPaths = files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);

        var unchanged = new List<ChangedFile>();
        if (removed.Any)
        {
            foreach (var path in paths.Where(p => !changedPaths.Contains(p) && !tree!.Symlinks.Contains(p) && RulesFormats.FromFileName(p) is not null)
                .OrderBy(p => p.Count(c => c == '/')).ThenBy(p => p, StringComparer.Ordinal).Take(MaxUnchangedRulesFiles))
            {
                var text = await budget.ReadAsync(path, () => gateway.GetFileContentAsync(pr, path, cancellationToken));
                if (text is not null)
                {
                    contents[path] = text;
                    unchanged.Add(new ChangedFile(path, FileChangeStatus.Unchanged, HeadContent: text));
                }
            }
        }

        var planning = new RepoSnapshot(paths, p => contents.GetValueOrDefault(p));
        var needed = changedRules.Concat(unchanged)
            .SelectMany(f => MissingIdentifiers.ManifestsNeeded(RulesFile.Parse(f.HeadContent!, RulesFormats.FromFileName(f.Path)!.Value), f.Path, planning))
            .Where(p => !contents.ContainsKey(p))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (needed.Count > MaxManifestFetches)
        {
            notes.Add($"R006: read {MaxManifestFetches} of {needed.Count} manifest files; references needing the rest were not checked.");
        }

        foreach (var path in needed.Take(MaxManifestFetches))
        {
            contents[path] = await budget.ReadAsync(path, () => gateway.GetFileContentAsync(pr, path, cancellationToken));
        }

        return (new RepoSnapshot(paths, p => contents.GetValueOrDefault(p)), unchanged);
    }

    internal static bool NeedsHeadContent(ChangedFile file) =>
        RulesFileLint.IsRulesFile(file)
        || (file.Status != FileChangeStatus.Removed
            && file.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            && (file.Patch?.Contains('[') ?? false));
}

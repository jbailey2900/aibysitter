using System.Reflection;

namespace Aibysitter.Rules.PullRequests;

public enum ReviewConclusion
{
    Success,
    Neutral,
    Failure,
}

/// <param name="Notes">Summary notes from checks (<see cref="INotesSource"/>).</param>
public sealed record PullRequestReview(IReadOnlyList<PullRequestFinding> Findings, ReviewConclusion Conclusion, IReadOnlyList<string>? Notes = null);

/// <summary>A check that adds notes to the check run summary.</summary>
public interface INotesSource
{
    IEnumerable<string> Notes(PullRequestContext context);
}

public sealed class PullRequestReviewer
{
    public PullRequestReviewer()
        : this(DiscoverChecks())
    {
    }

    public PullRequestReviewer(IEnumerable<IPullRequestCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);
        Checks = checks.OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
    }

    public IReadOnlyList<IPullRequestCheck> Checks { get; }

    public PullRequestReview Review(PullRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var findings = Checks
            .Where(c => context.Config.IsEnabled(c.Id))
            .SelectMany(c => c.Evaluate(ForCheck(c.Id, context)))
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .ThenBy(f => f.Line)
            .ThenBy(f => f.CheckId, StringComparer.Ordinal)
            .ToList();

        var notes = Checks
            .Where(c => context.Config.IsEnabled(c.Id))
            .OfType<INotesSource>()
            .SelectMany(c => c.Notes(context))
            .ToList();

        return new PullRequestReview(findings, Conclude(findings, context.Config), notes);
    }

    /// <summary>The context with files that <c>ignore</c> excludes from this check removed.</summary>
    private static PullRequestContext ForCheck(string checkId, PullRequestContext context) =>
        context.Config.Ignore.Count == 0 || RepoConfig.PathOnlyChecks.Contains(checkId)
            ? context
            : context with { Files = context.Files.Where(f => !context.Config.IsIgnored(checkId, f.Path)).ToList() };

    private ReviewConclusion Conclude(IReadOnlyList<PullRequestFinding> findings, RepoConfig config)
    {
        if (findings.Count == 0)
        {
            return ReviewConclusion.Success;
        }

        if (config.FailsCheck)
        {
            var severityByCheck = Checks.ToDictionary(c => c.Id, c => c.Severity, StringComparer.Ordinal);
            if (findings.Any(f => Fails(f.CheckId, f.SeverityOr(severityByCheck[f.CheckId]), config.Conclusion)))
            {
                return ReviewConclusion.Failure;
            }
        }

        return ReviewConclusion.Neutral;
    }

    /// <summary>
    /// fail-on-errors: Error findings fail. fail-on-warnings: Warning and Error findings fail, except P014, which fails
    /// only at Error. Info never fails.
    /// </summary>
    public static bool Fails(string checkId, Severity severity, ConclusionMode mode) => mode switch
    {
        ConclusionMode.FailOnErrors => severity == Severity.Error,
        ConclusionMode.FailOnWarnings => severity == Severity.Error || (severity == Severity.Warning && checkId != RulesFileLint.CheckId),
        _ => false,
    };

    public static IReadOnlyList<IPullRequestCheck> DiscoverChecks() =>
        typeof(IPullRequestCheck).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IPullRequestCheck).IsAssignableFrom(t))
            .Where(t => t.GetConstructor(BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes) is not null)
            .Select(t => (IPullRequestCheck)Activator.CreateInstance(t)!)
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .ToList();
}

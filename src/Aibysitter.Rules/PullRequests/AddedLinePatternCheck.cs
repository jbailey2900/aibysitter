using System.Text.RegularExpressions;

namespace Aibysitter.Rules.PullRequests;

/// <summary>
/// Base for checks that flag added lines matching a pattern. One finding per line.
/// The message quotes the named group "m" when the pattern defines it, otherwise the whole match.
/// Generated files (<see cref="CodeText.IsGenerated"/>) are skipped.
/// </summary>
public abstract class AddedLinePatternCheck : IPullRequestCheck
{
    public abstract string Id { get; }
    public abstract string Title { get; }
    public abstract Severity Severity { get; }

    protected abstract Regex Pattern { get; }
    protected abstract string MessagePrefix { get; }
    protected abstract string FixHint { get; }

    protected abstract bool AppliesTo(string path);

    /// <summary>The matches on one added line that become a finding. Default: all.</summary>
    protected virtual IEnumerable<string> Keep(ChangedFile file, DiffLine line, IReadOnlyList<string> matches) => matches;

    public virtual IEnumerable<PullRequestFinding> Evaluate(PullRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        foreach (var file in context.Files.Where(f => f.Status != FileChangeStatus.Removed && AppliesTo(f.Path) && !CodeText.IsGenerated(f)))
        {
            foreach (var line in file.AddedLines)
            {
                var matches = Pattern.Matches(line.Text).Select(m => (m.Groups["m"].Success ? m.Groups["m"].Value : m.Value).Trim()).Distinct().ToList();
                matches = Keep(file, line, matches).ToList();
                if (matches.Count > 0)
                {
                    yield return new PullRequestFinding(
                        Id,
                        file.Path,
                        line.NewLine!.Value,
                        $"{MessagePrefix}: {string.Join(", ", matches.Select(m => $"\"{m}\""))}",
                        FixHint);
                }
            }
        }
    }
}

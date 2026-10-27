using System.Text.RegularExpressions;

namespace Aibysitter.Rules.PullRequests;

/// <param name="Workflow">The workflow file that references <paramref name="Replacement"/>; null when the replacement is a test file.</param>
public sealed record DeletedTestJustification(string Removed, string Replacement, string? Workflow);

/// <summary>
/// P008 justifications: description lines <c>Aibysitter: P008 &lt;removed test path&gt; -&gt; &lt;replacement path&gt;</c>.
/// Accepted when the removed path has a P008 finding, the head file list was fetched, the replacement exists at the head,
/// and the replacement is a test file or a workflow file at the head contains its path.
/// </summary>
public static partial class DeletedTestJustifications
{
    public const string Workflows = ".github/workflows/";

    /// <summary>Removed path and replacement, in description order, with <c>\</c> as <c>/</c> and no leading <c>./</c>.</summary>
    public static IReadOnlyList<(string Removed, string Replacement)> Parse(string? description) =>
        description is null
            ? []
            : LineRegex().Matches(description.Replace("\r\n", "\n")).Select(m => (NormalizePath(m.Groups["removed"].Value), NormalizePath(m.Groups["replacement"].Value))).ToList();

    private static string NormalizePath(string path)
    {
        path = path.Replace('\\', '/');
        return path.StartsWith("./", StringComparison.Ordinal) ? path[2..] : path;
    }

    /// <summary>True when some justification can only be accepted through a workflow reference.</summary>
    public static bool NeedsWorkflows(string? description) => Parse(description).Any(j => !FileKinds.IsTestFile(j.Replacement));

    public static bool IsWorkflowFile(string path) =>
        path.StartsWith(Workflows, StringComparison.Ordinal)
        && (path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase));

    /// <param name="flagged">Removed test files P008 flags before justifications.</param>
    public static (IReadOnlyDictionary<string, DeletedTestJustification> Accepted, IReadOnlyList<string> Notes) Evaluate(
        PullRequestContext context, IReadOnlyCollection<string> flagged)
    {
        ArgumentNullException.ThrowIfNull(context);

        var accepted = new Dictionary<string, DeletedTestJustification>(StringComparer.Ordinal);
        var notes = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var head = context.HeadFiles is null ? null : new HashSet<string>(context.HeadFiles, StringComparer.Ordinal);

        foreach (var (removed, replacement) in Parse(context.Description))
        {
            var reason = !seen.Add(removed) ? "duplicate line for this path"
                : !flagged.Contains(removed) ? "no P008 finding for that path"
                : head is null ? "the head file list is unavailable"
                : !head.Contains(replacement) ? $"{replacement} is not in the head commit"
                : null;

            if (reason is null)
            {
                var workflow = FileKinds.IsTestFile(replacement)
                    ? null
                    : context.WorkflowFiles?.Where(w => w.Value.Replace('\\', '/').Contains(replacement, StringComparison.Ordinal)).Select(w => w.Key).Order(StringComparer.Ordinal).FirstOrDefault();
                if (!FileKinds.IsTestFile(replacement) && workflow is null)
                {
                    reason = $"{replacement} is not a test file and no workflow under {Workflows} references it";
                }
                else
                {
                    accepted[removed] = new DeletedTestJustification(removed, replacement, workflow);
                    continue;
                }
            }

            notes.Add($"P008 justification for {removed} not applied: {reason}.");
        }

        return (accepted, notes);
    }

    [GeneratedRegex(@"^[ \t]*Aibysitter:[ \t]+P008[ \t]+(?<removed>\S+)[ \t]+->[ \t]+(?<replacement>\S+)[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex LineRegex();
}

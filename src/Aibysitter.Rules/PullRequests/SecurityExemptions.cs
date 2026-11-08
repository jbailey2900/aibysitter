using System.Text.RegularExpressions;

namespace Aibysitter.Rules.PullRequests;

/// <summary>
/// Added antiforgery and authorization exemptions in ASP.NET Core code outside test files: [IgnoreAntiforgeryToken],
/// [AllowAnonymous], .DisableAntiforgery(), .AllowAnonymous(). Comment lines and string literals are skipped.
/// An exemption that a removed line in the same hunk already carried is an edited line, not a new exemption.
/// Also authorization requirements removed: [Authorize], RequireAuthenticatedUser() and .RequireAuthorization() with no
/// added copy left in the file; and a policy or roles argument dropped ([Authorize(Roles = "Admin")] to [Authorize],
/// .RequireAuthorization(policy) to .RequireAuthorization()) in one hunk. [Authorize] on a Razor Pages handler is skipped
/// (ignored by the framework, MVC1001).
/// </summary>
public sealed partial class SecurityExemptions : AddedLinePatternCheck
{
    public override string Id => "P016";
    public override string Title => "Security exemptions";
    public override Severity Severity => Severity.Warning;

    protected override Regex Pattern => ExemptionRegex();
    protected override string MessagePrefix => "Security exemption added";
    protected override string FixHint => "Confirm the endpoint needs the exemption and that nothing else relies on the protection.";

    public const string RemovedFixHint = "Confirm who should reach the endpoint now, and that nothing else relied on the requirement.";

    public override IEnumerable<PullRequestFinding> Evaluate(PullRequestContext context) => base.Evaluate(context).Concat(Removals(context));

    private enum Requirement
    {
        Authorize,
        RequireAuthenticatedUser,
        RequireAuthorization,
    }

    private sealed record Occurrence(Requirement Kind, string Text, bool HasArguments, int Index, int Hunk);

    private IEnumerable<PullRequestFinding> Removals(PullRequestContext context)
    {
        foreach (var file in context.Files.Where(f => f.Status != FileChangeStatus.Removed && AppliesTo(f.Path) && !CodeText.IsGenerated(f)))
        {
            var lines = file.Lines;
            var hunkOf = new int[lines.Count];
            var h = 0;
            var position = 0;
            foreach (var hunk in Hunks(lines))
            {
                foreach (var _ in hunk)
                {
                    hunkOf[position++] = h;
                }

                h++;
            }

            var removed = Occurrences(lines, hunkOf, DiffLineKind.Removed);
            var added = Occurrences(lines, hunkOf, DiffLineKind.Added);

            // A policy or roles argument dropped in one hunk: removed with arguments, added without.
            var paired = new HashSet<Occurrence>();
            foreach (var bare in added.Where(a => !a.HasArguments && a.Kind is Requirement.Authorize or Requirement.RequireAuthorization))
            {
                var dropped = removed.FirstOrDefault(r => r.Kind == bare.Kind && r.HasArguments && r.Hunk == bare.Hunk && !paired.Contains(r));
                if (dropped is null)
                {
                    continue;
                }

                paired.Add(dropped);
                paired.Add(bare);
                if (!(bare.Kind == Requirement.Authorize && OnHandler(lines, bare.Index)))
                {
                    yield return new PullRequestFinding(Id, file.Path, lines[bare.Index].NewLine!.Value, $"Authorization requirement dropped: \"{dropped.Text}\" -> \"{bare.Text}\"", RemovedFixHint);
                }
            }

            // Removed with no added copy left in the file (an attribute moved within the file is not removed).
            foreach (var kind in Enum.GetValues<Requirement>())
            {
                var gone = removed.Where(r => r.Kind == kind && !paired.Contains(r)).ToList();
                var kept = added.Count(a => a.Kind == kind && !paired.Contains(a));
                var unmatched = gone.Count - kept;
                if (unmatched <= 0)
                {
                    continue;
                }

                var sameHunk = added.Where(a => a.Kind == kind && !paired.Contains(a)).Select(a => a.Hunk).ToHashSet();
                foreach (var r in gone.OrderBy(r => sameHunk.Contains(r.Hunk) ? 1 : 0).Take(unmatched).OrderBy(r => r.Index))
                {
                    if (kind == Requirement.Authorize && OnHandler(lines, r.Index))
                    {
                        continue;
                    }

                    yield return new PullRequestFinding(Id, file.Path, Anchor(lines, r.Index), $"Authorization removed: \"{r.Text}\"", RemovedFixHint);
                }
            }
        }
    }

    private static List<Occurrence> Occurrences(IReadOnlyList<DiffLine> lines, int[] hunkOf, DiffLineKind kind)
    {
        var result = new List<Occurrence>();
        for (var i = 0; i < lines.Count; i++)
        {
            var text = lines[i].Text;
            if (lines[i].Kind != kind || CodeText.IsCommentOnly(text))
            {
                continue;
            }

            foreach (Match m in RequirementRegex().Matches(text))
            {
                if (CodeText.IsInsideStringLiteral(text, m.Index))
                {
                    continue;
                }

                var requirement = m.Groups["authorize"].Success ? Requirement.Authorize
                    : m.Groups["user"].Success ? Requirement.RequireAuthenticatedUser
                    : Requirement.RequireAuthorization;
                var hasArguments = m.Groups["args"].Success && m.Groups["args"].Value.Trim().Length > 0;
                result.Add(new Occurrence(requirement, m.Value.Trim(), hasArguments, i, hunkOf[i]));
            }
        }

        return result;
    }

    /// <summary>The next code line after the attribute (skipping attributes, comments and blank lines) is a Razor Pages handler.</summary>
    private static bool OnHandler(IReadOnlyList<DiffLine> lines, int index)
    {
        var kind = lines[index].Kind;
        for (var i = index + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Kind != DiffLineKind.Context && line.Kind != kind)
            {
                continue;
            }

            var text = line.Text.Trim();
            if (text.Length == 0 || text.StartsWith('[') || CodeText.IsCommentOnly(line.Text))
            {
                continue;
            }

            return HandlerRegex().IsMatch(text);
        }

        return false;
    }

    /// <summary>The new-file line after a removed line, else the one before it, else 1.</summary>
    private static int Anchor(IReadOnlyList<DiffLine> lines, int index)
    {
        for (var i = index + 1; i < lines.Count; i++)
        {
            if (lines[i].NewLine is { } next)
            {
                return next;
            }
        }

        for (var i = index - 1; i >= 0; i--)
        {
            if (lines[i].NewLine is { } previous)
            {
                return previous;
            }
        }

        return 1;
    }

    protected override bool AppliesTo(string path) =>
        !FileKinds.IsTestFile(path) && Path.GetExtension(path).ToLowerInvariant() is ".cs" or ".cshtml" or ".razor";

    protected override IEnumerable<string> Keep(ChangedFile file, DiffLine line, IReadOnlyList<string> matches)
    {
        var carried = Carried(file);
        return Exemptions(line.Text, matches).Where(m => !(carried.TryGetValue((line.NewLine!.Value, Normalize(m)), out var n) && n > 0)).ToList();
    }

    private static IEnumerable<string> Exemptions(string text, IEnumerable<string> matches) =>
        CodeText.IsCommentOnly(text)
            ? []
            : matches.Where(m => !CodeText.IsInsideStringLiteral(text, text.IndexOf(m, StringComparison.Ordinal)));

    private static string Normalize(string exemption) => string.Concat(exemption.Where(c => !char.IsWhiteSpace(c)));

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<ChangedFile, Dictionary<(int Line, string Exemption), int>> carried = new();

    /// <summary>
    /// Added-line exemptions matched by one on a removed line of the same hunk, keyed by new line number. Each removed
    /// exemption covers one added exemption, in order, so a second copy added in the hunk is still flagged.
    /// </summary>
    private Dictionary<(int Line, string Exemption), int> Carried(ChangedFile file) => carried.GetValue(file, f =>
    {
        var result = new Dictionary<(int, string), int>();
        foreach (var hunk in Hunks(f.Lines))
        {
            var removed = hunk.Where(l => l.Kind == DiffLineKind.Removed)
                .SelectMany(l => Exemptions(l.Text, ExemptionRegex().Matches(l.Text).Select(m => m.Value)))
                .GroupBy(Normalize)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

            foreach (var line in hunk.Where(l => l.Kind == DiffLineKind.Added))
            {
                foreach (var exemption in Exemptions(line.Text, ExemptionRegex().Matches(line.Text).Select(m => m.Value)).Select(Normalize).Distinct())
                {
                    if (removed.TryGetValue(exemption, out var left) && left > 0)
                    {
                        removed[exemption] = left - 1;
                        result[(line.NewLine!.Value, exemption)] = 1;
                    }
                }
            }
        }

        return result;
    });

    /// <summary>Diff lines split into hunks: a hunk ends where old or new line numbers stop being consecutive.</summary>
    internal static IEnumerable<List<DiffLine>> Hunks(IReadOnlyList<DiffLine> lines)
    {
        var hunk = new List<DiffLine>();
        int? nextOld = null, nextNew = null;
        foreach (var line in lines)
        {
            var continues = hunk.Count > 0
                && (line.OldLine is not { } o || o == nextOld)
                && (line.NewLine is not { } n || n == nextNew);
            if (!continues && hunk.Count > 0)
            {
                yield return hunk;
                hunk = [];
            }

            hunk.Add(line);
            nextOld = line.OldLine + 1 ?? nextOld;
            nextNew = line.NewLine + 1 ?? nextNew;
        }

        if (hunk.Count > 0)
        {
            yield return hunk;
        }
    }

    [GeneratedRegex(@"(?<authorize>\[\s*Authorize\b(?:\s*\((?<args>[^)]*)\))?\s*\]?)|(?<user>\bRequireAuthenticatedUser\s*\(\s*\))|\.\s*RequireAuthorization\s*\((?<args>[^)]*)\)")]
    private static partial Regex RequirementRegex();

    [GeneratedRegex(@"\bOn(?:Get|Post|Put|Delete|Patch)\w*\s*\(")]
    private static partial Regex HandlerRegex();

    [GeneratedRegex(@"\[\s*(?:IgnoreAntiforgeryToken|AllowAnonymous)\b|\.\s*(?:DisableAntiforgery|AllowAnonymous)\s*\(\s*\)")]
    private static partial Regex ExemptionRegex();
}

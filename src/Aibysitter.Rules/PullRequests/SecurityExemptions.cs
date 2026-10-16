using System.Text.RegularExpressions;

namespace Aibysitter.Rules.PullRequests;

/// <summary>
/// Added antiforgery and authorization exemptions in ASP.NET Core code outside test files: [IgnoreAntiforgeryToken],
/// [AllowAnonymous], .DisableAntiforgery(), .AllowAnonymous(). Comment lines and string literals are skipped.
/// An exemption that a removed line in the same hunk already carried is an edited line, not a new exemption.
/// </summary>
public sealed partial class SecurityExemptions : AddedLinePatternCheck
{
    public override string Id => "P016";
    public override string Title => "Security exemptions";
    public override Severity Severity => Severity.Warning;

    protected override Regex Pattern => ExemptionRegex();
    protected override string MessagePrefix => "Security exemption added";
    protected override string FixHint => "Confirm the endpoint needs the exemption and that nothing else relies on the protection.";

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

    [GeneratedRegex(@"\[\s*(?:IgnoreAntiforgeryToken|AllowAnonymous)\b|\.\s*(?:DisableAntiforgery|AllowAnonymous)\s*\(\s*\)")]
    private static partial Regex ExemptionRegex();
}

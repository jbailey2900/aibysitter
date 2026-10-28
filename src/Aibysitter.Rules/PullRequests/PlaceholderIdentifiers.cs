using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Aibysitter.Rules.PullRequests;

public sealed partial class PlaceholderIdentifiers : AddedLinePatternCheck
{
    public override string Id => "P001";
    public override string Title => "Placeholder identifiers";
    public override Severity Severity => Severity.Error;

    protected override Regex Pattern => PlaceholderRegex();
    protected override string MessagePrefix => "Placeholder left in change";
    protected override string FixHint => "Replace with the real identifier or value.";

    /// <summary>Code and config files outside test files.</summary>
    protected override bool AppliesTo(string path) => AppliesToPath(path);

    private static bool AppliesToPath(string path) => FileKinds.IsCodeOrConfig(path) && !FileKinds.IsTestFile(path);

    /// <summary>
    /// The head content decides whether the file is a guarded or documented template: P001 applies to the file and an added line
    /// holds a placeholder. Without it, the guard and documenting comments are looked for in the diff only.
    /// </summary>
    public static bool NeedsHeadContent(ChangedFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return file.Status != FileChangeStatus.Removed && AppliesToPath(file.Path) && file.AddedLines.Any(l => PlaceholderRegex().IsMatch(l.Text));
    }

    /// <summary>
    /// Not findings: comment lines (including inside block comments and docstrings), test-data attributes
    /// ([InlineData], [TestCase], [DataRow]), placeholders that a comment line in the same file names (a documented template),
    /// and every placeholder in a file that refuses to run while one is left (<see cref="IsGuardedTemplate"/>).
    /// </summary>
    protected override IEnumerable<string> Keep(ChangedFile file, DiffLine line, IReadOnlyList<string> matches)
    {
        if (guarded.GetValue(file, f => new StrongBox<bool>(IsGuardedTemplate(f))).Value)
        {
            return [];
        }

        var (commentLines, documented) = Comments(file);
        return commentLines.Contains(line.NewLine!.Value) || TestDataRegex().IsMatch(line.Text)
            ? []
            : matches.Where(m => !documented.Contains(m));
    }

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<ChangedFile, Tuple<IReadOnlySet<int>, HashSet<string>>> comments = new();

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<ChangedFile, StrongBox<bool>> guarded = new();

    /// <summary>Lines after a guard line searched for its throw or exit.</summary>
    public const int GuardWindow = 10;

    /// <summary>
    /// A line tests a value against a quoted literal containing <c>::</c> (beyond any placeholder in it), and that line or one of
    /// the next <see cref="GuardWindow"/> lines throws or exits, with no blank line or <c>GO</c> between: T-SQL
    /// <c>LIKE N'%::%'</c> + <c>THROW</c> (also after a multi-line condition), PowerShell <c>-like '*::*'</c> + <c>throw</c>,
    /// shell <c>== *"::"*</c> or <c>grep -q '::'</c> + <c>exit</c>. Read from the head content when fetched, otherwise from the
    /// diff's added and context lines.
    /// </summary>
    internal static bool IsGuardedTemplate(ChangedFile file)
    {
        var lines = file.HeadContent is { } head
            ? head.Replace("\r\n", "\n").Split('\n')
            : file.Lines.Where(l => l.Kind != DiffLineKind.Removed).Select(l => l.Text).ToArray();

        for (var i = 0; i < lines.Length; i++)
        {
            if (GuardTestRegex().IsMatch(lines[i])
                && LiteralRegex().Matches(lines[i]).Any(m => PlaceholderRegex().Replace(m.Value, "").Contains("::", StringComparison.Ordinal))
                && StopsWithin(lines, i))
            {
                return true;
            }
        }

        return false;
    }

    private static bool StopsWithin(string[] lines, int guard)
    {
        for (var j = guard; j < lines.Length && j <= guard + GuardWindow; j++)
        {
            if (j > guard && (lines[j].Trim().Length == 0 || lines[j].Trim().Equals("GO", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (StopRegex().IsMatch(lines[j]))
            {
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex(@"(?i)\blike\b|-match\b|\bcontains\b|\bgrep\b|\bincludes\b|\bindexof\b|==|=~")]
    private static partial Regex GuardTestRegex();

    [GeneratedRegex(@"'[^'\n]*'|""[^""\n]*""")]
    private static partial Regex LiteralRegex();

    [GeneratedRegex(@"(?i)\b(?:throw|raiserror|exit|die|abort)\b")]
    private static partial Regex StopRegex();

    /// <summary>Comment line numbers, and the placeholders those lines name.</summary>
    private (IReadOnlySet<int> Lines, HashSet<string> Documented) Comments(ChangedFile file)
    {
        var entry = comments.GetValue(file, f =>
        {
            var lines = CodeText.CommentLines(f);
            var texts = f.HeadContent is { } head
                ? head.Replace("\r\n", "\n").Split('\n').Where((_, i) => lines.Contains(i + 1))
                : f.Lines.Where(l => l.NewLine is { } n && lines.Contains(n)).Select(l => l.Text);
            var documented = texts.SelectMany(t => PlaceholderRegex().Matches(t).Select(m => m.Value)).ToHashSet(StringComparer.Ordinal);
            return Tuple.Create(lines, documented);
        });
        return (entry.Item1, entry.Item2);
    }

    [GeneratedRegex(@"^\s*\[\s*(?:[\w.]+(?:\([^)]*\))?\s*,\s*)*(?:InlineData|TestCase|DataRow)(?:Attribute)?\s*\(")]
    private static partial Regex TestDataRegex();

    [GeneratedRegex(@"::[A-Z][A-Z0-9_]*::|\bREPLACE_ME\b|\bYOUR_[A-Z0-9_]+\b|(?i:<placeholder>|<your[-_ ][^>]+>)")]
    private static partial Regex PlaceholderRegex();
}

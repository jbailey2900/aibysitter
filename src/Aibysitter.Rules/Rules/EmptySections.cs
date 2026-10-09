using System.Text.RegularExpressions;

namespace Aibysitter.Rules.Rules;

/// <summary>
/// A heading with no content before the next heading of the same or higher level, or before end of file.
/// Content: any line other than blank lines, suppression comments and HTML comments; a deeper heading; a level-1
/// heading directly after it whose own section has content (wrapper for an embedded document).
/// Not sections: the file's first heading when it is level 1; a heading whose text is an instruction of four or more
/// words, or contains inline code; headings after a prose line ending in ":" until the next prose line (examples);
/// in .cursorrules and .windsurfrules, a heading line directly above or below another heading of the same level (comment block).
/// </summary>
public sealed partial class EmptySections : IRule
{
    /// <summary>Fewest words for a heading to be read as an instruction rather than a section name.</summary>
    public const int MinInstructionWords = 4;

    public string Id => "R011";
    public string Title => "Empty sections";
    public Severity Severity => Severity.Warning;

    public IEnumerable<Finding> Evaluate(RulesFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var lines = file.Lines;
        var empty = new bool[lines.Count];
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (lines[i].IsHeading)
            {
                empty[i] = IsEmpty(lines, empty, i);
            }
        }

        var plainText = file.Format is RulesFormat.CursorRules or RulesFormat.WindsurfRules;
        var introduced = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (!line.IsHeading)
            {
                if (line.IsProse && !IsComment(line))
                {
                    introduced = line.Text.TrimEnd().EndsWith(':');
                }

                continue;
            }

            var level = Level(line.Text);
            var exempt = (level == 1 && !lines.Take(i).Any(l => l.IsHeading))
                || introduced
                || IsInstructionHeading(line.Text)
                || (plainText && (IsSameLevelHeading(lines, i - 1, level) || IsSameLevelHeading(lines, i + 1, level)));

            if (empty[i] && !exempt)
            {
                yield return new Finding(
                    Id,
                    line.Number,
                    $"Section \"{HeadingText(line.Text)}\" has no content.",
                    "Add the rules for this section, or delete the heading.");
            }
        }
    }

    private static bool IsEmpty(IReadOnlyList<RulesLine> lines, bool[] empty, int i)
    {
        var level = Level(lines[i].Text);
        for (var j = i + 1; j < lines.Count; j++)
        {
            if (lines[j].IsHeading)
            {
                var next = Level(lines[j].Text);
                return next < level ? next > 1 || empty[j] : next == level;
            }

            if (!lines[j].IsBlank && !IsComment(lines[j]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsComment(RulesLine line) => line.IsDirective || InstructionText.IsHtmlComment(line.Text);

    private static bool IsInstructionHeading(string text)
    {
        var name = HeadingText(text);
        return name.Contains('`') || (WordRegex().Count(name) >= MinInstructionWords && InstructionText.IsInstruction(name));
    }

    private static bool IsSameLevelHeading(IReadOnlyList<RulesLine> lines, int index, int level) =>
        index >= 0 && index < lines.Count && lines[index].IsHeading && Level(lines[index].Text) == level;

    private static string HeadingText(string text) => HeadingTextRegex().Replace(text, string.Empty).Trim();

    private static int Level(string text) => LevelRegex().Match(text).Groups[1].Value.Length;

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}'_.-]*")]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"^\s{0,3}(#{1,6})\s")]
    private static partial Regex LevelRegex();

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s+|(?<!\s)\s+#+\s*$")]
    private static partial Regex HeadingTextRegex();
}

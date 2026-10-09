using System.Text.RegularExpressions;

namespace Aibysitter.Rules.Rules;

/// <summary>
/// Rationale phrases in an instruction: a list item (with its continuation lines) that contains an instruction sentence,
/// or a paragraph sentence that is an instruction and the sentence after it (<see cref="InstructionText.IsInstruction"/>).
/// Skips HTML comment lines, quoted text, phrases after "/" or a quote mark (templates), "just / only / simply / merely /
/// solely / purely because", "because of", and "the reason" as an object (after report, give, state, log, include,
/// record, show, name, note or "with", or followed by punctuation, "and", "or" or end of line). One finding per line.
/// </summary>
public sealed partial class RationaleProse : IRule
{
    public string Id => "R001";
    public string Title => "Rationale prose";
    public Severity Severity => Severity.Info;

    public IEnumerable<Finding> Evaluate(RulesFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        foreach (var unit in InstructionText.Units(file))
        {
            var (text, starts) = InstructionText.Join(unit);
            var spans = InstructionText.SentenceSpans(text);
            var instruction = spans.Select(s => InstructionText.IsInstruction(text.Substring(s.Start, s.Length))).ToList();
            var inScope = InstructionText.IsListItem(unit[0].Text)
                ? Enumerable.Repeat(instruction.Contains(true), spans.Count).ToList()
                : instruction.Select((isInstruction, i) => isInstruction || (i > 0 && instruction[i - 1])).ToList();

            foreach (var line in unit.Where(l => !InstructionText.IsHtmlComment(l.Text)))
            {
                var terms = PhraseRegex().Matches(QuotedRegex().Replace(line.Text, m => new string(' ', m.Length)))
                    .Where(m => inScope[SentenceIndex(spans, starts[line] + m.Index)])
                    .Select(m => m.Value.ToLowerInvariant())
                    .Distinct()
                    .ToList();

                if (terms.Count > 0)
                {
                    yield return new Finding(Id, line.Number, $"Rationale prose: {string.Join(", ", terms.Select(t => $"\"{t}\""))}", "Remove the explanation. State the instruction only.");
                }
            }
        }
    }

    private static int SentenceIndex(IReadOnlyList<(int Start, int Length)> spans, int offset)
    {
        var index = 0;
        while (index + 1 < spans.Count && spans[index + 1].Start <= offset)
        {
            index++;
        }

        return index;
    }

    /// <summary>Quoted spans: "…", “…”, and '…' opened after a space, "(" or line start.</summary>
    [GeneratedRegex(@"""[^""]*""|“[^“”]*”|(?<=^|[\s(])'[^'\n]+'(?=[\s).,;:!?]|$)")]
    private static partial Regex QuotedRegex();

    [GeneratedRegex(@"\b(?=because|so\s+that|in\s+order\s+to|the\s+reason|this\s+ensures|this\s+helps|which\s+means)(?<![/""'“‘]\s*)(?<!\b(?:just|only|simply|merely|solely|purely)\s+)(?<!\b(?:report|reports|give|gives|state|states|log|logs|include|includes|record|records|show|shows|name|names|note|notes|with)\s+)\b(?:because(?!\s+of\b)|so that|in order to|the reason(?!\s*(?:[.,;:)]|and\b|or\b|$))|this ensures|this helps|which means)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PhraseRegex();
}

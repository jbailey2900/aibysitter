using System.Text.RegularExpressions;

namespace Aibysitter.Rules.Rules;

/// <summary>
/// Hedges that make an instruction optional, on lines an instruction sentence overlaps
/// (<see cref="InstructionText.InstructionLines"/>). Evaluated per clause. Skips table rows, inline code, links, paths,
/// quoted text, negated "try to", "can / could / may / might / would / will try to", "prefer to" with a named alternative
/// (not, instead of, over, rather than), "consider" outside suggestion position or used
/// as a label (followed by "=", ":" or "/"), and clauses that open with a third-person or plural subject.
/// </summary>
public sealed partial class HedgedInstructions : IRule
{
    public string Id => "R007";
    public string Title => "Hedged instructions";
    public Severity Severity => Severity.Warning;

    public IEnumerable<Finding> Evaluate(RulesFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var instructionLines = InstructionText.InstructionLines(file);
        foreach (var line in file.Lines.Where(l => instructionLines.Contains(l.Number)))
        {
            var hedges = new List<string>();
            foreach (var clause in InstructionText.Clauses(QuotedRegex().Replace(line.Text, " QUOTE ")))
            {
                if (!DescriptiveStartRegex().IsMatch(clause))
                {
                    hedges.AddRange(HedgeRegex().Matches(clause).Select(m => Normalize(m.Value)));
                }

                var consider = ConsiderRegex().Match(clause);
                var rest = consider.Success ? clause[(consider.Index + consider.Length)..] : string.Empty;
                if (consider.Success && !InstructionText.HasConcreteTarget(rest) && !ConsiderObjectRegex().IsMatch(rest) && !LabelUseRegex().IsMatch(rest))
                {
                    hedges.Add("consider");
                }
            }

            if (hedges.Count > 0)
            {
                yield return new Finding(
                    Id,
                    line.Number,
                    $"Hedge: {string.Join(", ", hedges.Distinct().Select(h => $"\"{h}\""))}",
                    "State the instruction without the hedge, or state the condition that makes it apply.");
            }
        }
    }

    private static string Normalize(string value) => WhitespaceRegex().Replace(value.ToLowerInvariant(), " ");

    [GeneratedRegex(@"\b(?=try\s+to\b)(?<!\b(?:not|never|don't|n't|can|could|may|might|would|will)\s+)try\s+to\b|\bif\s+possible\b|\bideally\b|\b(?:where|when|whenever)\s+possible\b|\bprefer\s+to\b(?!(?:(?!\bprefer\s+to\b).)*\b(?:not|instead\s+of|over|rather\s+than)\b)|\b(?:where|when|if|as)\s+appropriate\b", RegexOptions.IgnoreCase)]
    private static partial Regex HedgeRegex();

    /// <summary>"consider" as a suggestion: opening the clause, or after "you can / could / may / might / should".</summary>
    [GeneratedRegex(@"^(?:please\s+|also\s+)?consider\b|\byou\s+(?:can|could|may|might|should)\s+(?:also\s+)?consider\b", RegexOptions.IgnoreCase)]
    private static partial Regex ConsiderRegex();

    /// <summary>"consider whether / if / how" asks for an evaluation, not an optional action.</summary>
    [GeneratedRegex(@"^\s*(?:whether|if|how)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ConsiderObjectRegex();

    /// <summary>Clause opens with a third-person or plural subject ("Drivers use", "Handles"), not an instruction.</summary>
    [GeneratedRegex(@"^(?!(?:always|unless|is|was|has|does|this|its|as)\b)[A-Za-z-]+s\b", RegexOptions.IgnoreCase)]
    private static partial Regex DescriptiveStartRegex();

    /// <summary>"consider" as a label: followed by "=", ":" or "/".</summary>
    [GeneratedRegex(@"^\s*[=:/]")]
    private static partial Regex LabelUseRegex();

    [GeneratedRegex(@"""[^""]*""|“[^“”]*”")]
    private static partial Regex QuotedRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}

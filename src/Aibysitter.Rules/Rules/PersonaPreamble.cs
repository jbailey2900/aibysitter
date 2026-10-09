using System.Text.RegularExpressions;

namespace Aibysitter.Rules.Rules;

/// <summary>Role-play preambles ("You are an expert…", "Act as a senior…") outside code blocks, quotes, and inline code.</summary>
public sealed partial class PersonaPreamble : IRule
{
    public string Id => "R010";
    public string Title => "Persona preamble";
    public Severity Severity => Severity.Info;

    public IEnumerable<Finding> Evaluate(RulesFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        foreach (var line in file.Lines.Where(l => (l.IsProse || l.IsHeading) && !InstructionText.IsTableRow(l.Text)))
        {
            var match = PersonaRegex().Match(InstructionText.WithoutCode(QuotedRegex().Replace(line.Text, " QUOTE ")));
            if (match.Success)
            {
                yield return new Finding(
                    Id,
                    line.Number,
                    $"Persona preamble: \"{match.Value.Trim()}\"",
                    "Remove the persona. State the project's facts and rules.");
            }
        }
    }

    [GeneratedRegex(@"\b(?:you\s+are|you're|act\s+as|acting\s+as|behave\s+as|pretend\s+to\s+be|imagine\s+you\s+are)\s+(?:a|an|the)\s+(?:\w+[\s-]+){0,3}?(?:expert|senior|principal|staff|lead|world[\s-]class|seasoned|experienced|veteran|elite|10x|genius|master|guru|ninja|rockstar)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PersonaRegex();

    [GeneratedRegex(@"""[^""]*""|“[^“”]*”")]
    private static partial Regex QuotedRegex();
}

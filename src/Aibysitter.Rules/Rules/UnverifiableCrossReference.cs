using System.Text.RegularExpressions;

namespace Aibysitter.Rules.Rules;

/// <summary>
/// References to "above", "below", "earlier", or "the usual way" with no link, heading name, or inline code on the line.
/// </summary>
public sealed partial class UnverifiableCrossReference : IRule
{
    public string Id => "R014";
    public string Title => "Unverifiable cross-reference";
    public Severity Severity => Severity.Info;

    public IEnumerable<Finding> Evaluate(RulesFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        foreach (var line in file.Lines.Where(l => l.IsProse && !InstructionText.IsTableRow(l.Text)))
        {
            if (AnchorRegex().IsMatch(line.Text))
            {
                continue;
            }

            var match = ReferenceRegex().Match(line.Text);
            if (match.Success)
            {
                yield return new Finding(
                    Id,
                    line.Number,
                    $"Cross-reference without a target: \"{match.Value.Trim()}\"",
                    "Name the section, file, or command it refers to.");
            }
        }
    }

    [GeneratedRegex(@"\b(?:as\s+(?:mentioned|discussed|described|noted|stated|explained|shown|outlined)\s+(?:above|below|earlier|before|previously)|(?:mentioned|described|discussed|noted|outlined)\s+(?:above|earlier|previously)|see\s+(?:above|below)|(?:the|our)\s+usual\s+(?:way|approach|pattern|process|style)|the\s+(?:same\s+)?way\s+we\s+(?:always|usually)\b|like\s+before)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReferenceRegex();

    /// <summary>A markdown link, inline code, or a quoted / bold section name on the line counts as a target.</summary>
    [GeneratedRegex(@"\]\([^)\]]+\)|`[^`]+`|""[^""]+""|“[^“”]+”|\*\*[^*]+\*\*")]
    private static partial Regex AnchorRegex();
}

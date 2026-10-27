using System.Text.RegularExpressions;

namespace Aibysitter.Rules.PullRequests;

/// <param name="Reason">The accepted reason, cut to <see cref="SwallowedExceptionJustifications.MaxReasonLength"/>; null when rejected.</param>
/// <param name="Rejection">Why the marker does not apply; null when accepted.</param>
public sealed record SwallowedExceptionJustification(string? Reason, string? Rejection);

/// <summary>
/// P009 justifications: a comment <c>aibysitter-ignore P009: &lt;reason&gt;</c> (<c>//</c>, <c>#</c>, <c>/* */</c> or <c>&lt;!-- --&gt;</c>)
/// on the catch line or on the line above it. Accepted when the reason has at least <see cref="MinReasonLength"/> characters.
/// </summary>
public static partial class SwallowedExceptionJustifications
{
    public const int MinReasonLength = 10;
    public const int MaxReasonLength = 200;

    /// <summary>The marker on the catch line (after code) or on the line above (the whole line); null when neither has one.</summary>
    public static SwallowedExceptionJustification? Find(ChangedFile file, int catchLine)
    {
        ArgumentNullException.ThrowIfNull(file);

        var lines = file.Lines.Where(l => l.Kind != DiffLineKind.Removed && l.NewLine is { } n && (n == catchLine || n == catchLine - 1))
            .ToDictionary(l => l.NewLine!.Value, l => l.Text);

        var match = lines.TryGetValue(catchLine, out var own) ? TrailingRegex().Match(own) : Match.Empty;
        if (!match.Success && lines.TryGetValue(catchLine - 1, out var above))
        {
            match = WholeLineRegex().Match(above);
        }

        if (!match.Success)
        {
            return null;
        }

        var rest = match.Groups["rest"].Value.Trim();
        if (rest.Length > 0 && rest[0] != ':')
        {
            return new(null, "no ':' after P009");
        }

        var reason = rest.TrimStart(':').Trim();
        return reason.Length == 0 ? new(null, "no reason")
            : reason.Length < MinReasonLength ? new(null, $"reason under {MinReasonLength} characters")
            : new(reason.Length > MaxReasonLength ? reason[..MaxReasonLength] + "…" : reason, null);
    }

    /// <summary>The reason runs to the end of the line, or to <c>*/</c> or <c>--&gt;</c>.</summary>
    private const string Marker = @"(?://|\#|/\*|<!--)[ \t]*aibysitter-ignore[ \t]+(?-i:P009)\b(?<rest>(?:(?!\*/|-->).)*)";

    [GeneratedRegex(Marker, RegexOptions.IgnoreCase)]
    private static partial Regex TrailingRegex();

    [GeneratedRegex(@"^[ \t]*" + Marker, RegexOptions.IgnoreCase)]
    private static partial Regex WholeLineRegex();
}

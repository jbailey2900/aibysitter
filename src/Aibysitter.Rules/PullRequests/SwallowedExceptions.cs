using System.Text.RegularExpressions;

namespace Aibysitter.Rules.PullRequests;

/// <summary>
/// Empty catch blocks (C#, Java, JS/TS, and similar) and except-pass (Python) in added code.
/// Looks across up to four consecutive added lines. Comments inside the block do not count as handling.
/// Empty catches of OperationCanceledException / TaskCanceledException (cancellation) are not flagged.
/// A catch inside a string literal on its line is not code. Generated files are skipped.
/// A justification comment (<see cref="SwallowedExceptionJustifications"/>) downgrades the finding to Info.
/// </summary>
public sealed partial class SwallowedExceptions : IPullRequestCheck, INotesSource
{
    public const string FixHint = "Handle the exception, log it, or let it propagate.";

    private const int Window = 4;

    public string Id => "P009";
    public string Title => "Swallowed exceptions";
    public Severity Severity => Severity.Warning;

    public IEnumerable<PullRequestFinding> Evaluate(PullRequestContext context) =>
        Review(context).Select(r => r.Justification?.Reason is { } reason
            ? r.Finding with { Message = $"{r.Finding.Message} Justified: {reason}", Severity = Severity.Info }
            : r.Finding);

    public IEnumerable<string> Notes(PullRequestContext context) =>
        Review(context)
            .Where(r => r.Justification?.Rejection is not null)
            .Select(r => $"P009 justification at {r.Finding.Path}:{r.Finding.Line} not applied: {r.Justification!.Rejection}.");

    private IEnumerable<(PullRequestFinding Finding, SwallowedExceptionJustification? Justification)> Review(PullRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        foreach (var file in context.Files.Where(f => f.Status != FileChangeStatus.Removed && FileKinds.IsCode(f.Path) && !CodeText.IsGenerated(f)))
        {
            var added = file.AddedLines.ToList();
            var python = file.Path.EndsWith(".py", StringComparison.OrdinalIgnoreCase);

            for (var i = 0; i < added.Count; i++)
            {
                var start = python
                    ? ExceptStartRegex().Match(added[i].Text)
                    : CatchStartRegex().Matches(added[i].Text).FirstOrDefault(m => !CodeText.IsInsideStringLiteral(added[i].Text, m.Index));
                if (start is not { Success: true })
                {
                    continue;
                }

                var block = new List<string> { added[i].Text };
                for (var j = i + 1; j < added.Count && j < i + Window && added[j].NewLine == added[j - 1].NewLine + 1; j++)
                {
                    block.Add(added[j].Text);
                }

                var text = string.Join("\n", block);
                var match = (python ? ExceptPassRegex() : EmptyCatchRegex()).Match(text, start.Index);
                if (match.Success && match.Index < added[i].Text.Length + 1 && !CancellationRegex().IsMatch(match.Value))
                {
                    var line = added[i].NewLine!.Value;
                    yield return (
                        new PullRequestFinding(
                            Id,
                            file.Path,
                            line,
                            python ? "Exception swallowed: except block only passes." : "Exception swallowed: empty catch block.",
                            FixHint),
                        SwallowedExceptionJustifications.Find(file, line));
                }
            }
        }
    }

    [GeneratedRegex(@"\bcatch\b")]
    private static partial Regex CatchStartRegex();

    [GeneratedRegex(@"\(\s*(?:System\.(?:Threading\.Tasks\.)?)?(?:OperationCanceled|TaskCanceled)Exception\b")]
    private static partial Regex CancellationRegex();

    [GeneratedRegex(@"^\s*except\b")]
    private static partial Regex ExceptStartRegex();

    /// <summary>catch, optional (…), optional when (…), then { } containing only whitespace and comments. A // comment ends at a newline; no alternative overlaps another.</summary>
    [GeneratedRegex(@"\bcatch\b\s*(?:\([^)]*\))?\s*(?:when\s*\([^)]*\)\s*)?\{(?:\s|//[^\n]*\n|/\*(?:[^*]|\*(?!/))*\*/)*\}", RegexOptions.Singleline)]
    private static partial Regex EmptyCatchRegex();

    [GeneratedRegex(@"^[ \t]*except\b[^:\n]*:[ \t]*(?:#[^\n]*)?(?:\n[ \t]*(?:#[^\n]*)?)*?(?:\n)?[ \t]*pass\b[ \t]*(?:#[^\n]*)?$", RegexOptions.Multiline)]
    private static partial Regex ExceptPassRegex();
}

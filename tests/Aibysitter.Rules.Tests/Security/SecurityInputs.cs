namespace Aibysitter.Rules.Tests;

/// <summary>Adversarial inputs from notes/security-review-2026-10-09.md and the regex fuzz pass on this branch.</summary>
internal static class SecurityInputs
{
    public const int Long = 100_000;

    public sealed record Input(string Name, string Text);

    /// <param name="Key">Type.Method of the [GeneratedRegex]; <see cref="Exported"/> when the browser engine has it too.</param>
    public sealed record PatternInput(string Key, string Name, string Text, bool Exported);

    /// <summary>The report's exact inputs for the lint engine (finding 1).</summary>
    public static IReadOnlyList<Input> Report { get; } =
    [
        new("heading-3200", "# a" + new string(' ', 3_200) + "b"),
        new("heading-6400", "# a" + new string(' ', 6_400) + "b"),
    ];

    /// <summary>One line each, 100,000 characters; each must match its pattern in under the limit.</summary>
    public static IReadOnlyList<PatternInput> Patterns { get; } =
    [
        new("RulesFile.HeadingRegex", "heading", "# a" + new string(' ', Long) + "b", true),
        new("EmptySections.HeadingTextRegex", "heading-text", "# a" + new string(' ', Long) + "b", true),
        new("SecretPatterns.JwtRegex", "jwt", "eyJ" + Repeat("aaaaaaaaaa-", Long), true),
        new("SecretPatterns.UrlCredentialsRegex", "url-credentials", Repeat("a.", Long), true),
        new("InstructionText.LinkRegex", "link-brackets", new string('[', Long), true),
        new("InstructionText.LinkRegex", "link-parens", Repeat("[a](", Long), true),
        new("InstructionText.PathRegex", "path", Repeat("a.", Long), true),
        new("InstructionText.FileNameRegex", "file-name", Repeat("a-", Long), true),
        new("InstructionText.ClauseSplitRegex", "clause-split", "a" + new string(' ', Long) + "b", true),
        new("InstructionText.ModalRegex", "modal", new string(' ', Long), true),
        new("UnverifiableCrossReference.AnchorRegex", "anchor-links", Repeat("](", Long), true),
        new("UnverifiableCrossReference.AnchorRegex", "anchor-quotes", new string('“', Long), true),
        new("HedgedInstructions.HedgeRegex", "prefer-to", Repeat("prefer to ", Long), true),
        new("HedgedInstructions.HedgeRegex", "hedge-spaces", new string(' ', Long), true),
        new("HedgedInstructions.QuotedRegex", "hedge-quotes", new string('“', Long), true),
        new("RationaleProse.QuotedRegex", "rationale-quotes", new string('“', Long), true),
        new("RationaleProse.PhraseRegex", "rationale-spaces", new string(' ', Long), true),
        new("PersonaPreamble.QuotedRegex", "persona-quotes", new string('“', Long), true),
        new("VagueVerbs.QualifierRegex", "qualifier-spaces", new string(' ', Long), true),
        new("Manifests.MakeRuleRegex", "make-lines", Repeat("a b c d e f g h\n", Long), false),
        new("Manifests.MakeRuleRegex", "make-spaces", "a" + new string(' ', Long), false),
        new("Manifests.MakeIncludeRegex", "make-blank-lines", new string('\n', Long), false),
        new("RepoReferences.LinkTargetRegex", "repo-link-targets", Repeat("](", Long), false),
        new("RepoReferences.LinkRegex", "repo-link-brackets", new string('[', Long), false),
        new("RepoReferences.UrlRegex", "repo-url", Repeat("a.", Long), false),
        new("RepoReferences.CommandSplitRegex", "command-split", new string(' ', Long) + "!", false),
        new("NewDependencies.GoRequireRegex", "go-require", Repeat("a.", Long), false),
        new("SwallowedExceptions.ExceptPassRegex", "except-pass", new string('\n', Long) + "!", false),
        new("SwallowedExceptions.EmptyCatchRegex", "catch-line-comments", "catch{" + Repeat("//", Long), false),
        new("SwallowedExceptions.EmptyCatchRegex", "catch-block-comments", "catch{" + Repeat("/*", Long), false),
    ];

    /// <summary>The same shapes at 20,000 characters, for the parity suite (both engines, same findings).</summary>
    public static IReadOnlyList<Input> ParityLines { get; } =
        Patterns.Where(p => p.Exported).Select(p => new Input(p.Name, p.Text[..Math.Min(p.Text.Length, 20_000)])).DistinctBy(i => i.Name)
            .Append(new Input("trailing-ws", new string(' ', 20_000) + "x"))
            .ToList();

    public static string Repeat(string unit, int length) => string.Concat(Enumerable.Repeat(unit, length / unit.Length));
}

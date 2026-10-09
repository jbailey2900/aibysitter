namespace Aibysitter.Rules;

/// <param name="Version">Ruleset version; null for App-check entries, which do not bump it.</param>
public sealed record ChangelogEntry(int? Version, DateOnly Date, string Title, IReadOnlyList<string> Changes);

/// <summary>What changed in each ruleset version, and in the App's checks. Newest first.</summary>
public static class RulesetChangelog
{
    private static readonly DateOnly Launch = new(2026, 10, 1);

    public static IReadOnlyList<ChangelogEntry> Ruleset { get; } =
    [
        new(4, new DateOnly(2026, 10, 3), "R005 parallel sections; R008 limit 5 per 100 lines", [
            "R005: a repeat at the same position under section openers of the same kind and level is not reported. Section openers: headings, bold-label lines, list items with deeper lines under them.",
            "R008: limit 5 emphasis lines per 100 lines (was 3), minimum 3. Files citing RFC 2119 or BCP 14 still do not count MUST or REQUIRED.",
            "R001: known limits listed on its rule page.",
        ]),
        new(3, new DateOnly(2026, 10, 3), "Instruction lines only; precision fixes from corpus pass 2", [
            "Instruction sentence: a clause opens with a directive word or an imperative verb, or a label opens with a directive word; or it contains must, should, shall, need to, have to, is required, you can / could / may / might, or do not after no subject pronoun; or it is a short list item with no label, no verb form and no leading determiner, pronoun or gerund (\"- One concept per file.\"). Catalog entries (list items labelled with inline code or an identifier) and files with 20 or more MDX component lines are not instructions.",
            "R001: only in a list item that contains an instruction sentence, or in an instruction sentence and the sentence after it. Skips quoted text, \"just / only / simply / merely / solely / purely because\", \"because of\", and \"the reason\" as an object.",
            "R002: instruction lines only. Skips a named resource after clean up / handle / manage; a stated purpose or method after the verb; \"ensure\" after inline code or before \"to avoid / prevent\"; qualifiers in verify / check clauses, followed by a gerund or a purpose or condition, or after more / most / less / least; \"as needed\" after inline code.",
            "R005: repeated instruction lines only. Headings, HTML comments, wrapped continuation lines and indented code are not counted. Lines that occur three or more times, and repeats in blocks of the same shape, are not reported.",
            "R007: instruction lines only. Skips \"can / could / may / might / would / will try to\", \"prefer to\" with a named alternative, and \"consider\" used as a label.",
            "R008: MUST and REQUIRED are not counted in files that use RFC 2119 keywords.",
            "R009: pass, passwd, pwd, abc123, 123456, 12345678 and qwerty are placeholders.",
            "R011: a level-1 heading with content after a heading is content (embedded document). Exempt: headings that are instructions of four or more words or contain inline code; headings after a line ending in \":\"; adjacent same-level heading lines in .cursorrules and .windsurfrules.",
            "R013: only paragraphs in which at least a quarter of the sentences are instructions.",
        ]),
        new(2, Launch, "Manual Cursor rules move to R016 (Info)", [
            "R016 ManualCursorRule (Info): a Cursor rule with alwaysApply not true, no globs and no description. Message: \"Manual rule: Cursor includes it only when @-mentioned.\"",
            "R015 FrontmatterFields no longer reports this case (was Warning, \"Rule never applies automatically\").",
        ]),
        new(1, Launch, "First versioned ruleset", [
            "Rules R001–R005, R007–R015. Scoring: Error 10, Warning 4, Info 1 per finding; at most 30 per rule; severity caps Error 40, Warning 30, Info 10.",
            "Before versioning (#1): R001–R005.",
            "Before versioning (#7): scoring and grades.",
            "Before versioning (#16): suppression comments; severity caps.",
            "Before versioning (#17): R002 and R005 precision fixes from the corpus run.",
            "Before versioning (#18): R007 HedgedInstructions, R008 EmphasisInflation, R009 SecretsInRulesFile, R012 UnclosedCodeFence.",
            "Before versioning (#21): R010 PersonaPreamble, R011 EmptySections, R013 ProseParagraph, R014 UnverifiableCrossReference.",
            "Before versioning (#22): format detection for Cursor, Copilot, Gemini and Windsurf files; R015 FrontmatterFields.",
            "Before versioning (#26): browser lint engine with the same results as the server.",
        ]),
    ];

    public static IReadOnlyList<ChangelogEntry> AppChecks { get; } =
    [
        new(null, new DateOnly(2026, 10, 9), "Review errors and read limits (security review)", ["Under fail-on-warnings or fail-on-errors, a review that fails with an error closes the check as failure; advisory stays neutral.", "Files over 100 KB or not UTF-8 are not read, and a review reads at most 2 MB of file content; the summary names the files not read.", "Text from the pull request is escaped in the check summary and the PR comment."]),
        new(null, new DateOnly(2026, 10, 9), "Pattern limits (security review)", ["Glob patterns in scope and ignore hold at most 8 wildcards; .gitignore lines with more are skipped for R006.", "P009: a // comment ends at the line break, so { // comment } on one line is not an empty catch.", "A check that runs past the 1-second pattern limit stops the review; the summary says so."]),
        new(null, new DateOnly(2026, 10, 8), "Repo config: conclusion fail-on-warnings", ["Fails the check on any Warning or Error finding, or a config error. P014 fails only when a rule fires at Error. Info findings never fail the check."]),
        new(null, new DateOnly(2026, 10, 8), "P003 AssertNothingTests withdrawn", ["No true positives in dogfood 1–3. The ID stays reserved."]),
        new(null, new DateOnly(2026, 10, 8), "P012 DebugLeftovers withdrawn", ["No true positives in dogfood 1–3 or corpus PR pass 1. The ID stays reserved."]),
        new(null, new DateOnly(2026, 10, 8), "P001 drops TODO_… (corpus PR pass 1)", ["TODO_ names are not flagged."]),
        new(null, new DateOnly(2026, 10, 8), "P007 skips string literals (corpus PR pass 1)", ["A suppression inside a string literal, on its line or in a multi-line literal opened earlier, is not flagged."]),
        new(null, new DateOnly(2026, 10, 8), "P008 skips whole-package removals (corpus PR pass 1)", ["A removed test file is not flagged when the folder above its test folder, or the folder X its test project X.Tests is named after, holds no code files at the head."]),
        new(null, new DateOnly(2026, 10, 7), "Config read from the base branch", ["A pull request is reviewed with the base branch's .github/aibysitter.json. When it changes the file, the summary says so and config errors are those of the new version."]),
        new(null, new DateOnly(2026, 10, 7), "Repo config: ignore", ["Path globs, or {paths, checks} objects, skipped by content checks. Not applied to P004, P008, P011, P013, P014. Up to 50 entries; listed in the summary."]),
        new(null, new DateOnly(2026, 10, 7), "P017 LoosenedAssertions withdrawn", ["No true positives on pull request diffs in two dogfood passes. The ID stays reserved. The check would need per-commit review: loosened assertions were inside test files added by the same pull request."]),
        new(null, new DateOnly(2026, 10, 7), "P002 skips string literals", ["A TODO, FIXME or stub pattern inside a string literal on its line is not flagged."]),
        new(null, new DateOnly(2026, 10, 7), "P015 UnpinnedActions (dogfood 2)", ["GitHub Actions referenced by tag or branch instead of a commit SHA. Warning."]),
        new(null, new DateOnly(2026, 10, 7), "P016 SecurityExemptions (dogfood 2)", ["Added [IgnoreAntiforgeryToken], [AllowAnonymous], .DisableAntiforgery() and .AllowAnonymous(). Warning."]),
        new(null, new DateOnly(2026, 10, 7), "P017 LoosenedAssertions (dogfood 2)", ["An exact test assertion replaced by a weaker one in the same hunk. Warning."]),
        new(null, new DateOnly(2026, 10, 7), "P018 BrowserPolicyLoosened (dogfood 2)", ["'unsafe-inline', 'unsafe-eval', AllowAnyOrigin() and Access-Control-Allow-Origin set to *. Warning."]),
        new(null, new DateOnly(2026, 10, 7), "P019 ConfigTodos (dogfood 2)", ["TBD, TODO or FIXME in config files. Warning."]),
        new(null, new DateOnly(2026, 10, 7), "P001 skips test files and documented templates (dogfood 2)", ["Test files are not checked. Comment lines inside block comments and docstrings are skipped, and so is a placeholder that a comment line in the same file names."]),
        new(null, new DateOnly(2026, 10, 7), "P003 counts asserting helpers; severity Info (dogfood 2)", ["A call to a method in the same file whose body asserts counts as an assertion. Severity Info, previously Warning."]),
        new(null, new DateOnly(2026, 10, 7), "P005 skips local connection strings in CI config (dogfood 2)", ["In CI config files, a connection string to localhost, 127.0.0.1, (local) or . is not flagged."]),
        new(null, new DateOnly(2026, 10, 7), "P012 drops print( (dogfood 2)", ["print( is not flagged."]),
        new(null, new DateOnly(2026, 10, 7), "Check runs retry 404 on update (#63)", ["A 404 from a check-run update is retried after 1, 2 and 4 s. A review whose check run could not be closed is queued again after 60 s, up to 3 attempts."]),
        new(null, new DateOnly(2026, 10, 3), "Generated files skipped (dogfood 1)", ["Files with <auto-generated in the first 10 lines are skipped by P001–P003, P006, P007, P009, P010 and P012."]),
        new(null, new DateOnly(2026, 10, 3), "P001 skips comments and test data (dogfood 1)", ["Comment-only lines and test-data attributes ([InlineData], [TestCase], [DataRow]) are not flagged."]),
        new(null, new DateOnly(2026, 10, 3), "P003 severity Warning (dogfood 1)", ["Assert-nothing tests are Warning, previously Error."]),
        new(null, new DateOnly(2026, 10, 3), "P005 connection-string passwords in code (dogfood 1)", ["In code files, a Password= or Pwd= value counts only inside a string literal."]),
        new(null, new DateOnly(2026, 10, 3), "P008 skips tests deleted with their subject (dogfood 1)", ["A deleted test file is not flagged when the pull request also removes the code file it is named after."]),
        new(null, new DateOnly(2026, 10, 3), "P009 skips catch in string literals (dogfood 1)", ["A catch inside a string literal on its line is not code."]),
        new(null, new DateOnly(2026, 10, 3), "P010 one finding per package (dogfood 1)", ["One finding per package per ecosystem; with central package management it is on the PackageVersion line."]),
        new(null, new DateOnly(2026, 10, 3), "P012 skips print( in script folders (dogfood 1)", ["print( in Python files under a scripts/ or tools/ folder is not flagged."]),
        new(null, new DateOnly(2026, 10, 3), "Check run title counts notices (dogfood 1)", ["The title adds \"N notices\" when Info findings exist."]),
        new(null, new DateOnly(2026, 10, 3), "P014 skips symlinked rules files", ["A rules file whose tree mode is 120000 is not linted; the summary notes \"P014 skipped <path>: symlink to <target>.\" GitHub returns the target's content under the link's path, so findings were reported at the link with the target's line numbers.", "P005 and R009: values equal to pass, passwd, pwd, abc123, 123456, 12345678 or qwerty are treated as placeholders."]),
        new(null, Launch, "R006 MissingIdentifiers (#24)", ["App-only. Paths, package scripts, make targets and MSBuild targets named in rules files are checked against the repository."]),
        new(null, Launch, "P014 RulesFileLint (#23)", ["Lints rules files changed in the pull request with the lint rules. Severity follows each rule."]),
        new(null, Launch, "P008–P012 (#20)", ["Deleted tests, swallowed exceptions, new dependencies, CI config edited, debug leftovers."]),
        new(null, Launch, "P005–P007, P013 (#19)", ["Secrets in diff, skipped tests, suppressed diagnostics, committed artifacts."]),
        new(null, Launch, "P001–P004 (#8)", ["Placeholder identifiers, TODO stubs, assert-nothing tests (C#), out-of-scope files."]),
    ];
}

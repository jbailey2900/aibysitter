using System.Text.RegularExpressions;

namespace Aibysitter.Rules.PullRequests;

public sealed partial class SkippedTests : AddedLinePatternCheck
{
    public override string Id => "P006";
    public override string Title => "Skipped tests";
    public override Severity Severity => Severity.Error;

    protected override Regex Pattern => SkipRegex();
    protected override string MessagePrefix => "Test skipped";
    protected override string FixHint => "Fix the test or the code under test. Remove the skip.";

    protected override bool AppliesTo(string path) => FileKinds.IsCode(path);

    /// <summary>
    /// xUnit Skip =, NUnit / MSTest [Ignore], JS it/test/describe.skip and xit / xtest / xdescribe,
    /// pytest / unittest skip, Go t.Skip, Rust #[ignore], JUnit @Disabled / @Ignore.
    /// it/test/describe.skip( only with no argument, a string title or true: Playwright's test.skip(condition, …) is conditional.
    /// </summary>
    [GeneratedRegex(@"\[\s*(?:Fact|Theory)\s*\(\s*Skip\s*=|\[\s*(?:[\w.]+(?:\([^()]*\))?\s*,\s*)*Ignore\s*[(\]]|\b(?:it|test|describe)\.skip\s*\(\s*(?:\)|['""`]|true\b)|\bx(?:it|test|describe)\s*\(|@pytest\.mark\.skip\b(?!if)|\bpytest\.skip\s*\(|@unittest\.skip\b(?!If|Unless)|\bt\.Skip(?:f|Now)?\s*\(|#\[\s*ignore\b|@(?:Disabled|Ignore)\b")]
    private static partial Regex SkipRegex();
}

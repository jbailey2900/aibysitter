namespace Aibysitter.Rules.Tests;

/// <summary>Copy that describes shipped features as future.</summary>
public class PageCopyTests
{
    private static readonly string PagesRoot = Path.Combine(Parity.NodeRunner.RepoRoot, "src", "Aibysitter.Web", "Pages");

    public static TheoryData<string> Phrases => new() { "Planned", "coming soon", "not yet" };

    [Theory]
    [MemberData(nameof(Phrases))]
    public void NoPage_SaysFeatureIsFuture(string phrase)
    {
        var hits = Directory.EnumerateFiles(PagesRoot, "*.cshtml", SearchOption.AllDirectories)
            .SelectMany(path => File.ReadLines(path).Select((line, i) => (path, line, number: i + 1)))
            .Where(x => x.line.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            .Select(x => $"{Path.GetRelativePath(PagesRoot, x.path)}:{x.number}")
            .ToList();

        Assert.True(hits.Count == 0, $"\"{phrase}\" in: {string.Join(", ", hits)}");
    }
}

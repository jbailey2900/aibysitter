using Aibysitter.Rules.PullRequests;
using Aibysitter.Rules.Repo;

namespace Aibysitter.Rules.Tests;

public class GlobRedosTests
{
    private const string Pattern = "*a*a*a*a*a*a*b";

    [Theory]
    [InlineData(60)]
    [InlineData(80)]
    [InlineData(10_000)]
    public void Glob_ManyWildcards_Fast(int length)
    {
        var glob = new Glob(Pattern);
        var path = new string('a', length);

        Timing.AssertFast($"glob {length}", () => glob.IsMatch(path));
        Assert.False(glob.IsMatch(path));
        Assert.True(glob.IsMatch(path + "b"));
    }

    [Theory]
    [InlineData(80)]
    [InlineData(10_000)]
    public void GitIgnore_ManyWildcards_Fast(int length)
    {
        var ignore = new GitIgnore();
        ignore.Add(Pattern, "");
        var path = new string('a', length);

        Timing.AssertFast($"gitignore {length}", () => ignore.IsIgnored(path));
        Assert.True(ignore.IsIgnored(path + "b"));
    }

    [Theory]
    [InlineData("*/*/*/*/*/*/*/*.cs", null)]
    [InlineData("**/a?/*/*/*/*/*/*.cs", null)]
    [InlineData("*/*/*/*/*/*/*/*/*.cs", "at most 8 wildcards (*, **, ?) per pattern")]
    [InlineData("a?b?c?d?e?f?g?h?i?", "at most 8 wildcards (*, **, ?) per pattern")]
    public void Glob_WildcardCap(string pattern, string? error) => Assert.Equal(error, Glob.Validate(pattern));

    [Fact]
    public void RepoConfig_ReportsWildcardCap()
    {
        var (_, errors) = RepoConfig.Parse("""{ "scope": ["*/*/*/*/*/*/*/*/*.cs"] }""");

        Assert.Contains(errors, e => e.Message.Contains("at most 8 wildcards", StringComparison.Ordinal));
    }

    [Fact]
    public void GitIgnore_SkipsLineOverCap_KeepsOthers()
    {
        var ignore = new GitIgnore();
        ignore.Add("*/*/*/*/*/*/*/*/*.log\nbin/", "");

        Assert.False(ignore.IsIgnored("a/b/c/d/e/f/g/h/x.log"));
        Assert.True(ignore.IsIgnored("bin/x.dll"));
    }
}

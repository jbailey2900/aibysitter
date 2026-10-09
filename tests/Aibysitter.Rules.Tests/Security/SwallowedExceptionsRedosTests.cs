using Aibysitter.Rules.PullRequests;
using static Aibysitter.Rules.Tests.PullRequests.PrTestData;

namespace Aibysitter.Rules.Tests;

public class SwallowedExceptionsRedosTests
{
    [Theory]
    [InlineData(24)]
    [InlineData(1_000)]
    [InlineData(50_000)]
    public void ManyCommentStarts_Fast(int count)
    {
        var context = Context(Added("src/A.cs", "try { Run(); }", "catch{" + SecurityInputs.Repeat("//", count * 2)));

        Timing.AssertFast($"catch{{ + //x{count}", () => new SwallowedExceptions().Evaluate(context).ToList());
    }

    [Fact]
    public void UnclosedBlockComments_Fast()
    {
        var context = Context(Added("src/A.cs", "catch {" + SecurityInputs.Repeat("/*", 100_000)));

        Timing.AssertFast("catch { + /* x50000", () => new SwallowedExceptions().Evaluate(context).ToList());
    }

    [Theory]
    [InlineData("catch {", "    // ignored", "}")]
    [InlineData("catch (Exception) { /* ignored */ }")]
    [InlineData("catch (IOException ex) when (ex.HResult == 5) {", "    /* a", "       b */", "}")]
    public void EmptyWithComments_StillFlagged(params string[] lines) =>
        Assert.Single(new SwallowedExceptions().Evaluate(Context(Added("src/A.cs", lines))));

    [Fact]
    public void LineCommentBeforeBraceOnSameLine_NotEmpty() =>
        Assert.Empty(new SwallowedExceptions().Evaluate(Context(Added("src/A.cs", "catch { // c }", "Log();"))));
}

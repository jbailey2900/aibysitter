using Aibysitter.Rules.PullRequests;
using static Aibysitter.Rules.Tests.PullRequests.PrTestData;

namespace Aibysitter.Rules.Tests.PullRequests;

/// <summary>P009 justification comments: aibysitter-ignore P009: &lt;reason&gt; on the catch line or the line above.</summary>
public class SwallowedExceptionJustificationTests
{
    private const string Reason = "best-effort temp cleanup";

    private static SwallowedExceptionJustification? Find(int catchLine, params string[] lines) => SwallowedExceptionJustifications.Find(Added("src/A.cs", lines), catchLine);

    [Theory]
    [InlineData($"catch (IOException) {{ }} // aibysitter-ignore P009: {Reason}")]
    [InlineData($"catch (IOException) {{ }} # aibysitter-ignore P009: {Reason}")]
    [InlineData($"catch (IOException) {{ /* aibysitter-ignore P009: {Reason} */ }}")]
    [InlineData($"catch (IOException) {{ }} <!-- aibysitter-ignore P009: {Reason} -->")]
    [InlineData($"catch (IOException) {{ }} // AIBYSITTER-IGNORE P009 : {Reason}  ")]
    public void OnCatchLine_Accepted(string line) => Assert.Equal(Reason, Find(1, line)?.Reason);

    [Theory]
    [InlineData($"// aibysitter-ignore P009: {Reason}")]
    [InlineData($"    # aibysitter-ignore P009: {Reason}")]
    [InlineData($"/* aibysitter-ignore P009: {Reason} */")]
    [InlineData($"<!-- aibysitter-ignore P009: {Reason} -->")]
    public void OnLineAbove_Accepted(string line) => Assert.Equal(Reason, Find(2, line, "catch (IOException) { }")?.Reason);

    [Fact]
    public void OnContextLineAbove_Accepted()
    {
        var file = new ChangedFile("src/A.cs", FileChangeStatus.Modified, $"@@ -9,2 +9,3 @@\n try {{ Delete(); }}\n // aibysitter-ignore P009: {Reason}\n+catch (IOException) {{ }}\n");

        Assert.Equal(Reason, SwallowedExceptionJustifications.Find(file, 11)?.Reason);
    }

    [Theory]
    [InlineData("// aibysitter-ignore P009:", "no reason")]
    [InlineData("// aibysitter-ignore P009", "no reason")]
    [InlineData("// aibysitter-ignore P009: too short", "reason under 10 characters")]
    [InlineData("// aibysitter-ignore P009 because cleanup is optional", "no ':' after P009")]
    public void InvalidMarker_Rejected(string line, string rejection)
    {
        var justification = Find(2, line, "catch (IOException) { }");

        Assert.Null(justification?.Reason);
        Assert.Equal(rejection, justification?.Rejection);
    }

    [Fact]
    public void TenCharacters_Accepted() => Assert.Equal("0123456789", Find(1, "catch { } // aibysitter-ignore P009: 0123456789")?.Reason);

    [Theory]
    [InlineData("// aibysitter-ignore P008: not this check at all")]
    [InlineData("// aibysitter-ignore p009: lower-case check id")]
    [InlineData("Cleanup(); // aibysitter-ignore P009: code before the comment")]
    [InlineData("// a comment about the catch below")]
    public void NotAMarker_Null(string line) => Assert.Null(Find(2, line, "catch (IOException) { }"));

    [Fact]
    public void MarkerTwoLinesAbove_Null() => Assert.Null(Find(3, $"// aibysitter-ignore P009: {Reason}", "", "catch (IOException) { }"));

    [Fact]
    public void LongReason_Cut()
    {
        var reason = Find(1, "catch { } // aibysitter-ignore P009: " + new string('x', 300))!.Reason!;

        Assert.Equal(SwallowedExceptionJustifications.MaxReasonLength + 1, reason.Length);
        Assert.EndsWith("…", reason);
    }
}

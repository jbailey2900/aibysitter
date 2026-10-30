using Aibysitter.Rules.PullRequests;
using Aibysitter.Web.Config;

namespace Aibysitter.Rules.Tests;

public class RepoConfigWriterTests
{
    [Fact]
    public void Defaults_WriteConclusionOnly()
    {
        Assert.Equal("{\n  \"conclusion\": \"advisory\"\n}\n", RepoConfigWriter.Write([], ConclusionMode.Advisory, []));
    }

    [Fact]
    public void AllKeys_InOrder_TwoSpaceIndent()
    {
        var json = RepoConfigWriter.Write(["src/**", "tests/**"], ConclusionMode.FailOnErrors, ["P002", "R004"]);

        Assert.Equal(
            "{\n  \"scope\": [\n    \"src/**\",\n    \"tests/**\"\n  ],\n  \"conclusion\": \"fail-on-errors\",\n  \"disable\": [\n    \"P002\",\n    \"R004\"\n  ]\n}\n",
            json);
    }

    [Fact]
    public void Output_RoundTripsThroughAppParser()
    {
        var json = RepoConfigWriter.Write(["src/**", "docs/*.md"], ConclusionMode.FailOnErrors, ["P002", "R006"]);

        var (config, errors) = RepoConfig.Parse(json);

        Assert.Empty(errors);
        Assert.Equal(["src/**", "docs/*.md"], config.Scope.Select(g => g.Pattern));
        Assert.Equal(ConclusionMode.FailOnErrors, config.Conclusion);
        Assert.Equal(["P002", "R006"], config.Disabled.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(ConclusionMode.Advisory, "advisory")]
    [InlineData(ConclusionMode.FailOnWarnings, "fail-on-warnings")]
    [InlineData(ConclusionMode.FailOnErrors, "fail-on-errors")]
    public void Conclusion_WrittenAndRoundTrips(ConclusionMode mode, string value)
    {
        var json = RepoConfigWriter.Write([], mode, []);

        Assert.Equal("{\n  \"conclusion\": \"" + value + "\"\n}\n", json);
        Assert.Equal(mode, RepoConfig.Parse(json).Config.Conclusion);
    }

    [Fact]
    public void Glob_WithQuotesAndNonAscii_IsEscapedOnlyWhereJsonRequires()
    {
        var json = RepoConfigWriter.Write(["docs/\"x\"/é+*.md"], ConclusionMode.Advisory, []);

        Assert.Contains("\"docs/\\\"x\\\"/é+*.md\"", json);
        Assert.Equal("docs/\"x\"/é+*.md", RepoConfig.Parse(json).Config.Scope.Single().Pattern);
    }

    [Fact]
    public void ScopeLines_TrimsDropsBlanksAndRepeats_KeepsOrder()
    {
        Assert.Equal(["src/**", "tests/**"], RepoConfigWriter.ScopeLines("  src/**\r\n\n tests/** \nsrc/**\n"));
        Assert.Empty(RepoConfigWriter.ScopeLines(null));
    }

    [Fact]
    public void Comment_WrittenLast_OnlyWhenTrue()
    {
        Assert.Equal("{\n  \"conclusion\": \"advisory\",\n  \"comment\": true\n}\n", RepoConfigWriter.Write([], ConclusionMode.Advisory, [], comment: true));
        Assert.DoesNotContain("comment", RepoConfigWriter.Write([], ConclusionMode.Advisory, [], comment: false));
        Assert.True(RepoConfig.Parse(RepoConfigWriter.Write(["src/**"], ConclusionMode.Advisory, ["P002"], comment: true)).Config.Comment);
    }

    [Fact]
    public void Ignore_WrittenAfterDisable_RoundTrips()
    {
        var json = RepoConfigWriter.Write(["src/**"], ConclusionMode.Advisory, ["P002"], comment: true, ignore: ["docs/**"]);

        Assert.Equal("{\n  \"scope\": [\n    \"src/**\"\n  ],\n  \"conclusion\": \"advisory\",\n  \"disable\": [\n    \"P002\"\n  ],\n  \"ignore\": [\n    \"docs/**\"\n  ],\n  \"comment\": true\n}\n", json);
        var (config, errors) = RepoConfig.Parse(json);
        Assert.Empty(errors);
        Assert.True(config.IsIgnored("P001", "docs/a.md"));
    }

    [Fact]
    public void ApprovedLines_NameVersionReason()
    {
        Assert.Equal(
            [("Polly", "[8.0,9.0)", "Retries for the payment client"), ("Serilog", "4.4.0", null), ("Jint", null, null)],
            RepoConfigWriter.ApprovedLines(" Polly [8.0,9.0)  Retries for the payment client \n\nSerilog 4.4.0\nJint"));
    }

    [Fact]
    public void Write_Approved_RoundTrips()
    {
        var json = RepoConfigWriter.Write([], ConclusionMode.FailOnWarnings, [], approved: [("Polly", "8.4.0", "Retries for the payment client")]);
        var (config, errors) = RepoConfig.Parse(json);

        Assert.Empty(errors);
        Assert.Equal(("Polly", "8.4.0", "Retries for the payment client"), (config.ApprovedDependencies[0].Name, config.ApprovedDependencies[0].Version.Text, config.ApprovedDependencies[0].Reason));
    }
}

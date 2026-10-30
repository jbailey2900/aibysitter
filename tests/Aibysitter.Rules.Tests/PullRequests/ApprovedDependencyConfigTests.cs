using Aibysitter.Rules.PullRequests;

namespace Aibysitter.Rules.Tests.PullRequests;

/// <summary>dependencies.approved in .github/aibysitter.json, and the version specs it accepts.</summary>
public class ApprovedDependencyConfigTests
{
    private static bool In(string spec, string version) =>
        VersionRange.TryParse(spec, out var range) && PackageVersion.TryParse(version, out var v) && range!.Contains(v!);

    [Theory]
    [InlineData("10.0.12", "10.0.12", true)]
    [InlineData("10.0.12", "10.0.13", false)]
    [InlineData("10.0", "10.0.0", true)]
    [InlineData("[10.0.12]", "10.0.12", true)]
    [InlineData("[10.0,11.0)", "10.0.0", true)]
    [InlineData("[10.0,11.0)", "10.9.99", true)]
    [InlineData("[10.0,11.0)", "11.0.0", false)]
    [InlineData("(10.0,11.0]", "10.0.0", false)]
    [InlineData("(10.0,11.0]", "11.0", true)]
    [InlineData("[8.0,)", "99.1", true)]
    [InlineData("(,2.0]", "1.0.0.5", true)]
    [InlineData("[1.0,2.0)", "2.0.0-preview.1", true)]
    [InlineData("[1.0,2.0)", "1.0.0-preview.1", false)]
    [InlineData("1.2.3.4", "1.2.3.4", true)]
    public void VersionRange_Contains(string spec, string version, bool expected) => Assert.Equal(expected, In(spec, version));

    [Theory]
    [InlineData("")]
    [InlineData("1.*")]
    [InlineData("[2.0,1.0]")]
    [InlineData("(1.0)")]
    [InlineData("[,]")]
    [InlineData("1.2.3.4.5")]
    [InlineData(">=1.0")]
    [InlineData("[1.0,2.0")]
    public void VersionRange_Invalid(string spec) => Assert.False(VersionRange.TryParse(spec, out _));

    [Fact]
    public void Parse_Entries()
    {
        var (config, errors) = RepoConfig.Parse("""
            {
              "dependencies": {
                "approved": [
                  { "name": "AspNet.Security.OAuth.Apple", "version": "10.0.0", "reason": "Sign in with Apple, plan step 10" },
                  { "name": "Microsoft.AspNetCore.Authentication.Google", "version": "[10.0,11.0)", "reason": "Google sign-in" }
                ]
              }
            }
            """);

        Assert.Empty(errors);
        Assert.Equal(["AspNet.Security.OAuth.Apple", "Microsoft.AspNetCore.Authentication.Google"], config.ApprovedDependencies.Select(a => a.Name));
        Assert.Equal("[10.0,11.0)", config.ApprovedDependencies[1].Version.Text);
        Assert.Equal("Google sign-in", config.ApprovedDependencies[1].Reason);
    }

    [Theory]
    [InlineData("""{ "name": "Polly", "reason": "retries" }""", "\"version\" is required")]
    [InlineData("""{ "name": "Polly", "version": "8.4.0" }""", "\"reason\" is required")]
    [InlineData("""{ "name": "Polly", "version": "8.4.0", "reason": "  " }""", "\"reason\" must be a non-empty string")]
    [InlineData("""{ "name": "Polly.*", "version": "8.4.0", "reason": "retries" }""", "wildcards are not allowed in \"name\" or \"version\"")]
    [InlineData("""{ "name": "Polly", "version": "8.*", "reason": "retries" }""", "wildcards are not allowed in \"name\" or \"version\"")]
    [InlineData("""{ "name": "Polly", "version": ">=8", "reason": "retries" }""", "\"version\" >=8 is not a version or a [min,max] range")]
    [InlineData("""{ "name": "Polly", "version": "8.4.0", "reason": "retries", "ecosystem": "nuget" }""", "unknown field \"ecosystem\"")]
    [InlineData("\"Polly\"", "must be an object with \"name\", \"version\" and \"reason\"")]
    public void Parse_InvalidEntry_ErrorOnItsLine_Skipped(string entry, string message)
    {
        var (config, errors) = RepoConfig.Parse($$"""
            {
              "dependencies": {
                "approved": [
                  { "name": "Serilog", "version": "4.4.0", "reason": "logging" },
                  {{entry}}
                ]
              }
            }
            """);

        var error = Assert.Single(errors);
        Assert.Equal(5, error.Line);
        Assert.EndsWith(message, error.Message);
        Assert.Equal(["Serilog"], config.ApprovedDependencies.Select(a => a.Name));
    }

    [Theory]
    [InlineData("""{ "dependencies": [] }""", "\"dependencies\" must be an object with \"approved\"")]
    [InlineData("""{ "dependencies": { "approved": {} } }""", "\"dependencies.approved\" must be an array of {\"name\", \"version\", \"reason\"} objects")]
    [InlineData("""{ "dependencies": { "blocked": [] } }""", "unknown key \"dependencies.blocked\"")]
    public void Parse_InvalidShape_Error(string json, string message) => Assert.EndsWith(message, Assert.Single(RepoConfig.Parse(json).Errors).Message);
}

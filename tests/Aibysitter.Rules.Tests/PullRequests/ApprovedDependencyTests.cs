using Aibysitter.Rules.PullRequests;
using static Aibysitter.Rules.Tests.PullRequests.PrTestData;

namespace Aibysitter.Rules.Tests.PullRequests;

/// <summary>P010 with dependencies.approved: a new reference whose version is in an entry for its name is Info.</summary>
public class ApprovedDependencyTests
{
    private static RepoConfig Config(params (string Name, string Version, string Reason)[] entries) =>
        RepoConfig.Parse("{\"conclusion\": \"fail-on-warnings\", \"dependencies\": {\"approved\": ["
            + string.Join(", ", entries.Select(e => $"{{\"name\": \"{e.Name}\", \"version\": \"{e.Version}\", \"reason\": \"{e.Reason}\"}}"))
            + "]}}").Config;

    private static PullRequestReview Review(RepoConfig config, params ChangedFile[] files) =>
        new PullRequestReviewer([new NewDependencies()]).Review(Context(config, files));

    private static ChangedFile Props(params string[] versions) =>
        new("Directory.Packages.props", FileChangeStatus.Modified, "@@ -8,1 +8," + (versions.Length + 1) + " @@\n <ItemGroup>\n" + string.Concat(versions.Select(v => "+    " + v + "\n")));

    private static ChangedFile Project(string path, params string[] references) =>
        new(path, FileChangeStatus.Modified, "@@ -12,1 +12," + (references.Length + 1) + " @@\n <ItemGroup>\n" + string.Concat(references.Select(r => "+    " + r + "\n")));

    /// <summary>iquitcooking.com #13: four PackageVersion entries and their PackageReference lines.</summary>
    private static ChangedFile[] Pr13() =>
    [
        Props(
            "<PackageVersion Include=\"Microsoft.AspNetCore.Identity.EntityFrameworkCore\" Version=\"10.0.12\" />",
            "<PackageVersion Include=\"Microsoft.AspNetCore.Authentication.Google\" Version=\"10.0.12\" />",
            "<PackageVersion Include=\"Microsoft.AspNetCore.Authentication.MicrosoftAccount\" Version=\"10.0.12\" />",
            "<PackageVersion Include=\"AspNet.Security.OAuth.Apple\" Version=\"10.0.0\" />"),
        Project("src/IQuitCooking.Data/IQuitCooking.Data.csproj", "<PackageReference Include=\"Microsoft.AspNetCore.Identity.EntityFrameworkCore\" />"),
        Project("src/IQuitCooking.Web/IQuitCooking.Web.csproj",
            "<PackageReference Include=\"Microsoft.AspNetCore.Authentication.Google\" />",
            "<PackageReference Include=\"Microsoft.AspNetCore.Authentication.MicrosoftAccount\" />",
            "<PackageReference Include=\"AspNet.Security.OAuth.Apple\" />"),
    ];

    [Fact]
    public void Pr13_AllApproved_InfoOnReferenceLines_Neutral()
    {
        var review = Review(
            Config(
                ("Microsoft.AspNetCore.Identity.EntityFrameworkCore", "10.0.12", "accounts, plan step 10"),
                ("Microsoft.AspNetCore.Authentication.Google", "[10.0,11.0)", "Google sign-in"),
                ("microsoft.aspnetcore.authentication.microsoftaccount", "10.0.12", "Microsoft sign-in"),
                ("AspNet.Security.OAuth.Apple", "10.0.0", "Sign in with Apple")),
            Pr13());

        Assert.All(review.Findings, f => Assert.Equal(Severity.Info, f.Severity));
        Assert.All(review.Findings, f => Assert.EndsWith(".csproj", f.Path));
        Assert.Contains(review.Findings, f => f.Message == "Approved dependency: Microsoft.AspNetCore.Authentication.Google (Google sign-in)");
        Assert.Equal(4, review.Findings.Count);
        Assert.Empty(review.Notes!);
        Assert.Equal(ReviewConclusion.Neutral, review.Conclusion);
    }

    [Fact]
    public void VersionOutsideEntry_WarningAndNote()
    {
        var review = Review(Config(("AspNet.Security.OAuth.Apple", "[9.0,10.0)", "Sign in with Apple")), Pr13());

        var apple = Assert.Single(review.Findings, f => f.Message.Contains("Apple", StringComparison.Ordinal));
        Assert.Null(apple.Severity);
        Assert.Equal("New dependency: AspNet.Security.OAuth.Apple", apple.Message);
        Assert.Equal(["Approved dependency entry for AspNet.Security.OAuth.Apple not applied: version 10.0.0 is outside [9.0,10.0)."], review.Notes);
        Assert.Equal(ReviewConclusion.Failure, review.Conclusion);
    }

    [Fact]
    public void ExistingPackageVersion_VersionUnknown_WarningAndNote()
    {
        var review = Review(Config(("Polly", "8.4.0", "retries")), Project("src/Web/Web.csproj", "<PackageReference Include=\"Polly\" />"));

        Assert.Null(Assert.Single(review.Findings).Severity);
        Assert.Equal(["Approved dependency entry for Polly not applied: the pull request does not show the version."], review.Notes);
    }

    [Fact]
    public void NonCentralVersionAttribute_Approved() =>
        Assert.Equal(Severity.Info, Assert.Single(Review(Config(("Polly", "8.4.0", "retries")), Project("src/Web/Web.csproj", "<PackageReference Include=\"Polly\" Version=\"8.4.0\" />")).Findings).Severity);

    [Fact]
    public void VersionOverride_TakesPrecedence()
    {
        var review = Review(Config(("Polly", "8.4.0", "retries")),
            Props("<PackageVersion Include=\"Polly\" Version=\"8.4.0\" />"),
            Project("src/Web/Web.csproj", "<PackageReference Include=\"Polly\" VersionOverride=\"8.5.0\" />"));

        Assert.Null(Assert.Single(review.Findings).Severity);
        Assert.Equal(["Approved dependency entry for Polly not applied: version 8.5.0 is outside 8.4.0."], review.Notes);
    }

    [Fact]
    public void FloatingVersion_NotApplied() =>
        Assert.Equal(["Approved dependency entry for Polly not applied: the pull request does not show the version."],
            Review(Config(("Polly", "8.4.0", "retries")), Project("src/Web/Web.csproj", "<PackageReference Include=\"Polly\" Version=\"8.*\" />")).Notes);

    [Fact]
    public void OtherPackage_NoNote()
    {
        var review = Review(Config(("Serilog", "4.4.0", "logging")), Project("src/Web/Web.csproj", "<PackageReference Include=\"Polly\" Version=\"8.4.0\" />"));

        Assert.Null(Assert.Single(review.Findings).Severity);
        Assert.Empty(review.Notes!);
    }

    [Fact]
    public void NpmPackage_NotMatched() =>
        Assert.Null(Assert.Single(Review(Config(("lodash", "4.17.21", "utility")),
            new ChangedFile("web/package.json", FileChangeStatus.Modified, "@@ -3,1 +3,2 @@\n   \"dependencies\": {\n+    \"lodash\": \"4.17.21\",\n")).Findings).Severity);

    [Fact]
    public void TransitivePin_Unchanged() =>
        Assert.Equal("Version pinned (transitive): Polly",
            Assert.Single(Review(Config(("Polly", "8.4.0", "retries")), Props("<PackageVersion Include=\"Polly\" Version=\"8.4.0\" />")).Findings).Message);
}

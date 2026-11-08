using Aibysitter.Rules.PullRequests;
using static Aibysitter.Rules.Tests.PullRequests.PrTestData;

namespace Aibysitter.Rules.Tests.PullRequests;

/// <summary>P016: authorization requirements removed, and policy or roles arguments dropped.</summary>
public class AuthorizationRemovedTests
{
    private static List<PullRequestFinding> Run(params ChangedFile[] files) => new SecurityExemptions().Evaluate(Context(files)).ToList();

    private static ChangedFile Modified(string path, string patch) => new(path, FileChangeStatus.Modified, patch);

    [Fact]
    public void AuthorizeRemovedFromClass_Flagged_OnNextLine()
    {
        var finding = Assert.Single(Run(Modified("src/Pages/Admin/Index.cshtml.cs", "@@ -5,3 +5,2 @@\n namespace App.Pages.Admin;\n-[Authorize(Roles = \"Admin\")]\n public class IndexModel : PageModel\n")));

        Assert.Equal(6, finding.Line);
        Assert.Equal("Authorization removed: \"[Authorize(Roles = \"Admin\")]\"", finding.Message);
        Assert.Equal(SecurityExemptions.RemovedFixHint, finding.FixHint);
    }

    [Fact]
    public void RolesDroppedFromAuthorize_Flagged_OnAddedLine()
    {
        var finding = Assert.Single(Run(Modified("src/Pages/Admin/Index.cshtml.cs", "@@ -5,3 +5,3 @@\n namespace App.Pages.Admin;\n-[Authorize(Roles = \"Admin\")]\n+[Authorize]\n public class IndexModel : PageModel\n")));

        Assert.Equal(6, finding.Line);
        Assert.Equal("Authorization requirement dropped: \"[Authorize(Roles = \"Admin\")]\" -> \"[Authorize]\"", finding.Message);
    }

    [Fact]
    public void PolicyDroppedFromRequireAuthorization_Flagged()
    {
        var patch = "@@ -40,3 +40,3 @@\n         group.MapGet(\"/notifications\", List)\n-        }).RequireAuthorization(ModerationPolicy);\n+        }).RequireAuthorization();\n         return app;\n";

        var finding = Assert.Single(Run(Modified("src/Notifications/NotificationEndpoints.cs", patch)));

        Assert.Equal(41, finding.Line);
        Assert.Equal("Authorization requirement dropped: \".RequireAuthorization(ModerationPolicy)\" -> \".RequireAuthorization()\"", finding.Message);
    }

    [Fact]
    public void RequireAuthenticatedUserRemoved_Flagged()
    {
        var patch = "@@ -10,4 +10,3 @@\n         options.AddPolicy(\"Staff\", policy => policy\n-            .RequireAuthenticatedUser()\n             .RequireRole(\"Moderator\"));\n";

        Assert.Equal("Authorization removed: \"RequireAuthenticatedUser()\"", Assert.Single(Run(Modified("src/Moderation/ModerationSetup.cs", patch))).Message);
    }

    [Fact]
    public void RequireAuthorizationRemoved_Flagged() =>
        Assert.Equal("Authorization removed: \".RequireAuthorization(\"Admin\")\"",
            Assert.Single(Run(Modified("src/Program.cs", "@@ -20,2 +20,2 @@\n-app.MapGet(\"/ops\", Ops).RequireAuthorization(\"Admin\");\n+app.MapGet(\"/ops\", Ops);\n"))).Message);

    [Fact]
    public void AuthorizeMovedWithinFile_NotFlagged()
    {
        var patch = "@@ -5,3 +5,2 @@\n namespace App.Pages;\n-[Authorize]\n public class OldModel : PageModel\n@@ -40,1 +39,2 @@\n }\n+[Authorize]\n";

        Assert.Empty(Run(Modified("src/Pages/Account.cshtml.cs", patch)));
    }

    [Fact]
    public void AuthorizeEditedWithArgumentsKept_NotFlagged() =>
        Assert.Empty(Run(Modified("src/Pages/Admin/Index.cshtml.cs", "@@ -5,2 +5,2 @@\n-[Authorize(Roles = \"Admin\")]\n+[Authorize(Roles = \"Admin\"), RequireHttps]\n public class IndexModel : PageModel\n")));

    [Fact]
    public void AuthorizeOnRazorPagesHandler_NotFlagged()
    {
        var patch = "@@ -470,4 +470,3 @@\n     }\n \n-    [Authorize]\n     public async Task<IActionResult> OnPostFeedbackAsync()\n";

        Assert.Empty(Run(Modified("TwoGoOut.Web/Pages/Index.cshtml.cs", patch)));
    }

    [Theory]
    [InlineData("-// [Authorize] is on the base class.")]
    [InlineData("-    var hint = \"[Authorize] required\";")]
    public void CommentOrStringLiteral_NotFlagged(string removed) =>
        Assert.Empty(Run(Modified("src/Pages/Account.cshtml.cs", $"@@ -5,2 +5,1 @@\n{removed}\n public class AccountModel : PageModel\n")));

    [Fact]
    public void TestFile_NotFlagged() =>
        Assert.Empty(Run(Modified("tests/App.Tests/Pages/AccountTests.cs", "@@ -5,2 +5,1 @@\n-[Authorize]\n public class FakePage : PageModel\n")));

    [Fact]
    public void RemovedFile_NotFlagged() =>
        Assert.Empty(Run(new ChangedFile("src/Pages/Old.cshtml.cs", FileChangeStatus.Removed, "@@ -1,2 +0,0 @@\n-[Authorize]\n-public class OldModel : PageModel\n")));

    [Fact]
    public void AuthorizeViewInRazor_NotFlagged() =>
        Assert.Empty(Run(Modified("src/Shared/Nav.razor", "@@ -3,2 +3,1 @@\n-<AuthorizeView>\n <p>Menu</p>\n")));

    [Fact]
    public void AllowAnonymousAdded_StillFlagged() =>
        Assert.Equal("Security exemption added: \"[AllowAnonymous\"",
            Assert.Single(Run(Modified("src/Pages/AccountClosed.cshtml.cs", "@@ -5,1 +5,2 @@\n namespace App.Pages;\n+[AllowAnonymous]\n"))).Message);
}

namespace Aibysitter.Rules.Tests;

public class DeployWorkflowTests
{
    private static string Repo(params string[] path) => File.ReadAllText(Path.Combine([Parity.NodeRunner.RepoRoot, .. path]));

    [Fact]
    public void DbConnection_IsASecret()
    {
        var workflow = Repo(".github", "workflows", "deploy.yml");

        Assert.Contains("DB_CONNECTION: ${{ secrets.DB_CONNECTION }}", workflow);
        Assert.DoesNotContain("vars.DB_CONNECTION", workflow);
        Assert.Contains("Repo secret `DB_CONNECTION`", Repo("docs", "self-hosting.md"));
    }
}

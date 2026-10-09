using System.Text.RegularExpressions;
using Aibysitter.Cli;

namespace Aibysitter.Rules.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CliEngineCollection
{
    public const string Name = "CLI engine factory";
}

[Collection(CliEngineCollection.Name)]
public class CliTimeoutTests
{
    private sealed class TimeoutRule : IRule
    {
        public string Id => "R099";

        public string Title => "Timeout";

        public Severity Severity => Severity.Info;

        public IEnumerable<Finding> Evaluate(RulesFile file) => throw new RegexMatchTimeoutException("x", "y", RegexTimeout.Default);
    }

    [Fact]
    public void Lint_Timeout_Exit4_MessageOnStderr()
    {
        var previous = CliApp.EngineFactory;
        CliApp.EngineFactory = () => new LintEngine([new TimeoutRule()]);
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exit = CliApp.Run(["lint", "-"], new StringReader("# Rules"), stdout, stderr);

            Assert.Equal(4, exit);
            Assert.Equal($"aibysitter: {RegexTimeout.LintMessage}", stderr.ToString().Trim());
            Assert.Empty(stdout.ToString());
        }
        finally
        {
            CliApp.EngineFactory = previous;
        }
    }

    [Fact]
    public void Help_ListsExitCode4() =>
        Assert.Contains("4 lint timed out.", CliApp.Usage);
}

using System.Text;
using System.Text.Json.Nodes;
using Aibysitter.Cli;

namespace Aibysitter.Rules.Tests.Cli;

public sealed class CliTests : IDisposable
{
    internal const string Contradiction = "# Rules\n- Always use tabs.\n- Never use tabs.\n";

    private readonly string dir = Directory.CreateTempSubdirectory("aibysitter-cli-").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    internal sealed record CliResult(int Exit, string Out, string Err);

    internal static CliResult Run(string[] args, string stdin = "")
    {
        var stdout = new StringWriter { NewLine = "\n" };
        var stderr = new StringWriter { NewLine = "\n" };
        var exit = CliApp.Run(args, new StringReader(stdin), stdout, stderr);
        return new CliResult(exit, stdout.ToString(), stderr.ToString());
    }

    private string File(string relative, string content) => File(relative, Encoding.UTF8.GetBytes(content));

    private string File(string relative, byte[] content)
    {
        var path = Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, content);
        return path;
    }

    private static JsonObject Json(CliResult result) => JsonNode.Parse(result.Out)!.AsObject();

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("help")]
    public void Help_ToStdout_Exit0(string arg)
    {
        var result = Run([arg]);

        Assert.Equal(CliApp.Ok, result.Exit);
        Assert.Contains("aibysitter lint <file|-> [options]", result.Out);
        Assert.Contains("Exit codes: 0 ok, 1 threshold failed (lint) or changes found (fix --dry-run), 2 usage error, 3 file not readable or not writable, 4 lint timed out.", result.Out);
        Assert.Contains("aibysitter init --packs <ids> --format <name>", result.Out);
        Assert.Empty(result.Err);
    }

    [Fact]
    public void NoArgs_UsageToStderr_Exit2()
    {
        var result = Run([]);

        Assert.Equal(CliApp.UsageError, result.Exit);
        Assert.Contains("Usage:", result.Err);
        Assert.Empty(result.Out);
    }

    [Fact]
    public void Version_ShowsToolAndRulesetVersion()
    {
        var result = Run(["--version"]);

        Assert.Equal(CliApp.Ok, result.Exit);
        Assert.Equal($"aibysitter 0.4.2 (ruleset v{RulesetVersion.Current})\n", result.Out);
    }

    [Theory]
    [InlineData(new[] { "scan", "x" }, "Unknown command \"scan\".")]
    [InlineData(new[] { "lint" }, "lint needs a file path, or - to read standard input.")]
    [InlineData(new[] { "lint", "a", "b" }, "lint takes one file; got \"a\" and \"b\".")]
    [InlineData(new[] { "lint", "a", "--nope" }, "Unknown option \"--nope\".")]
    [InlineData(new[] { "lint", "a", "--json=1" }, "Unknown option \"--json=1\".")]
    [InlineData(new[] { "lint", "a", "--format" }, "--format needs one of:")]
    [InlineData(new[] { "lint", "a", "--format", "Word" }, "--format needs one of:")]
    [InlineData(new[] { "lint", "a", "--format", "3" }, "--format needs one of:")]
    [InlineData(new[] { "lint", "a", "--disable" }, "--disable needs rule IDs")]
    [InlineData(new[] { "lint", "a", "--fail-below", "F" }, "--fail-below needs a grade: A, B, C or D.")]
    [InlineData(new[] { "lint", "a", "--fail-below", "AB" }, "--fail-below needs a grade: A, B, C or D.")]
    [InlineData(new[] { "lint", "-", "--disable", "R002,R999,P001" }, "Not a lint rule ID: R999, P001.")]
    [InlineData(new[] { "lint", "-", "--disable", "R006" }, "Not a lint rule ID: R006.")]
    public void UsageErrors_Exit2_MessageAndHint(string[] args, string message)
    {
        var result = Run(args, Contradiction);

        Assert.Equal(CliApp.UsageError, result.Exit);
        Assert.StartsWith("aibysitter: " + message, result.Err);
        Assert.EndsWith("Run 'aibysitter --help' for usage.\n", result.Err);
        Assert.Empty(result.Out);
    }

    [Fact]
    public void MissingFile_Exit3()
    {
        var path = Path.Combine(dir, "nope", "CLAUDE.md");
        var result = Run(["lint", path]);

        Assert.Equal(CliApp.FileError, result.Exit);
        Assert.Equal($"aibysitter: cannot read {path}: not found\n", result.Err);
    }

    [Fact]
    public void Directory_Exit3()
    {
        var result = Run(["lint", dir]);

        Assert.Equal(CliApp.FileError, result.Exit);
        Assert.Equal($"aibysitter: cannot read {dir}: is a directory\n", result.Err);
    }

    [Fact]
    public void Text_HeaderFindingsFix_Exit0ByDefault()
    {
        var path = File("CLAUDE.md", Contradiction + "- Handle errors properly.\n");
        var result = Run(["lint", path]);

        Assert.Equal(CliApp.Ok, result.Exit);
        Assert.Empty(result.Err);
        Assert.Equal(
            $"{path}: 86/100 B (CLAUDE.md, ruleset v{RulesetVersion.Current})\n" +
            $"{path}:3  Error    R003 ContradictoryModals  Contradicts line 2: \"use tabs\" is both required and forbidden.\n" +
            "    Fix: Keep one instruction and delete the other.\n" +
            $"{path}:4  Warning  R002 VagueVerbs           Vague wording: \"handle\", \"properly\"\n" +
            "    Fix: Name the concrete action, file, or command.\n",
            result.Out);
    }

    [Fact]
    public void Text_NoFindings_RulesOff_Suppressed()
    {
        var path = File("AGENTS.md", "<!-- aibysitter-disable R002 -->\n# Rules\n- Handle errors.\n- Use tabs.\n- Use tabs.\n");
        var result = Run(["lint", path, "--disable", "r005"]);

        Assert.Equal(
            $"{path}: 100/100 A (AGENTS.md, ruleset v{RulesetVersion.Current})\n" +
            "Rules off: R005.\n" +
            "No findings.\n" +
            "Suppressed (1), not scored:\n" +
            $"{path}:3  Warning  R002 VagueVerbs  Vague wording: \"handle\"\n",
            result.Out);
    }

    [Theory]
    [InlineData("CLAUDE.md", "ClaudeMd")]
    [InlineData("sub/dir/CLAUDE.md", "ClaudeMd")]
    [InlineData("AGENTS.md", "AgentsMd")]
    [InlineData("GEMINI.md", "GeminiMd")]
    [InlineData(".cursorrules", "CursorRules")]
    [InlineData(".windsurfrules", "WindsurfRules")]
    [InlineData(".github/copilot-instructions.md", "CopilotInstructions")]
    [InlineData("repo/.github/copilot-instructions.md", "CopilotInstructions")]
    [InlineData(".cursor/rules/style.mdc", "CursorMdc")]
    [InlineData("repo/.cursor/rules/nested/style.mdc", "CursorMdc")]
    [InlineData("style.mdc", "Markdown")]
    [InlineData("copilot-instructions.md", "Markdown")]
    [InlineData("notes.md", "Markdown")]
    public void Format_FromPath_AbsoluteAndNested(string relative, string expected)
    {
        var path = File(relative, "# Rules\n- Use tabs.\n");

        Assert.Equal(expected, Json(Run(["lint", path, "--json"]))["detectedFormat"]!.GetValue<string>());
    }

    [Fact]
    public void FormatOption_OverridesPath_AndUnknownPathFallsBackToContent()
    {
        var claude = File("CLAUDE.md", "# Rules\n- Use tabs.\n");
        var mdc = File("notes.txt", "---\ndescription: x\nalwaysApply: false\n---\n- Use tabs.\n");

        Assert.Equal("GeminiMd", Json(Run(["lint", claude, "--json", "--format", "geminimd"]))["detectedFormat"]!.GetValue<string>());
        Assert.Equal("CursorMdc", Json(Run(["lint", mdc, "--json"]))["detectedFormat"]!.GetValue<string>());
        Assert.Equal("ClaudeMd", Json(Run(["lint", mdc, "--json", "--format=ClaudeMd"]))["detectedFormat"]!.GetValue<string>());
    }

    [Fact]
    public void Stdin_LabelledStdin_ContentDetected()
    {
        var result = Run(["lint", "-", "--json"], "---\nglobs: src/**\n---\n- Use tabs.\n");
        var json = Json(result);

        Assert.Equal("<stdin>", json["file"]!.GetValue<string>());
        Assert.Equal("CursorMdc", json["detectedFormat"]!.GetValue<string>());
        Assert.StartsWith("<stdin>: ", Run(["lint", "-"], Contradiction).Out);
    }

    [Fact]
    public void Bom_AndCrlf_LintSameAsLf()
    {
        var lf = File("a/CLAUDE.md", Contradiction);
        var crlfBom = File("b/CLAUDE.md", [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Contradiction.Replace("\n", "\r\n"))]);

        var a = Json(Run(["lint", lf, "--json"]));
        var b = Json(Run(["lint", crlfBom, "--json"]));
        a.Remove("file");
        b.Remove("file");
        Assert.True(JsonNode.DeepEquals(a, b), $"{a}\n{b}");
    }

    [Fact]
    public void Json_FileFirst_ThenApiFields()
    {
        var path = File("CLAUDE.md", Contradiction);
        var json = Json(Run(["lint", path, "--json", "--disable", "R005,R001"]));

        Assert.Equal(["file", "rulesetVersion", "detectedFormat", "score", "grade", "disabled", "findings", "suppressed"], json.Select(p => p.Key));
        Assert.Equal(path, json["file"]!.GetValue<string>());
        Assert.Equal(RulesetVersion.Current, json["rulesetVersion"]!.GetValue<int>());
        Assert.Equal(["R001", "R005"], json["disabled"]!.AsArray().Select(n => n!.GetValue<string>()));
        var finding = json["findings"]![0]!.AsObject();
        Assert.Equal(["rule", "line", "severity", "message", "fixHint"], finding.Select(p => p.Key));
        Assert.Equal("R003", finding["rule"]!.GetValue<string>());
        Assert.Equal("Error", finding["severity"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(new string[0], CliApp.Ok, null)]
    [InlineData(new[] { "--fail-on-error" }, CliApp.ThresholdFailed, "aibysitter: failed: 1 Error finding.\n")]
    [InlineData(new[] { "--fail-below", "A" }, CliApp.Ok, null)]
    [InlineData(new[] { "--fail-below", "a", "--fail-on-error" }, CliApp.ThresholdFailed, "aibysitter: failed: 1 Error finding.\n")]
    public void Thresholds_GradeA_WithOneError(string[] flags, int exit, string? err)
    {
        // R003 (-10) and R001 (-1) give 89 B; disabling R001 leaves 90 A with one Error.
        var path = File("CLAUDE.md", Contradiction + "- Use spaces because they align.\n");
        var result = Run(["lint", path, "--disable", "R001", .. flags]);

        Assert.Equal("A", Json(Run(["lint", path, "--disable", "R001", "--json"]))["grade"]!.GetValue<string>());
        Assert.Equal(exit, result.Exit);
        Assert.Equal(err ?? "", result.Err);
    }

    [Theory]
    [InlineData("A", CliApp.ThresholdFailed)]
    [InlineData("B", CliApp.Ok)]
    [InlineData("C", CliApp.Ok)]
    [InlineData("D", CliApp.Ok)]
    public void FailBelow_AtGradeBoundary(string threshold, int exit)
    {
        // R003 (-10) and R001 (-1): 89, grade B.
        var path = File("CLAUDE.md", Contradiction + "- Use spaces because they align.\n");
        var result = Run(["lint", path, "--fail-below", threshold]);

        Assert.StartsWith($"{path}: 89/100 B", result.Out);
        Assert.Equal(exit, result.Exit);
        Assert.Equal(exit == CliApp.Ok ? "" : "aibysitter: failed: grade B is below A.\n", result.Err);
    }

    [Fact]
    public void BothThresholds_ReportedTogether()
    {
        var path = File("CLAUDE.md", Contradiction + "- Use spaces because they align.\n");
        var result = Run(["lint", path, "--fail-on-error", "--fail-below", "A"]);

        Assert.Equal(CliApp.ThresholdFailed, result.Exit);
        Assert.Equal("aibysitter: failed: 1 Error finding; grade B is below A.\n", result.Err);
    }

    [Fact]
    public void SuppressedErrors_DoNotFail()
    {
        var path = File("CLAUDE.md", "<!-- aibysitter-disable R003 -->\n" + Contradiction);

        Assert.Equal(CliApp.Ok, Run(["lint", path, "--fail-on-error"]).Exit);
    }

    [Fact]
    public void NoLengthLimit()
    {
        var path = File("CLAUDE.md", "# Rules\n" + string.Concat(Enumerable.Range(0, 6000).Select(i => $"- Rule {i} uses library number {i} for parsing input files.\n")));

        var json = Json(Run(["lint", path, "--json"]));
        Assert.Contains(json["findings"]!.AsArray(), f => f!["rule"]!.GetValue<string>() == "R004");
        Assert.True(new FileInfo(path).Length > 100_000);
    }
}

/// <summary>Tests that change the process working directory; not run in parallel with anything else.</summary>
[Collection(nameof(WorkingDirectoryCollection))]
public sealed class CliWorkingDirectoryTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("aibysitter-cli-").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private string File(string relative, string content)
    {
        var path = Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    private static JsonObject Json(CliTests.CliResult result) => JsonNode.Parse(result.Out)!.AsObject();

    private static CliTests.CliResult Run(string[] args) => CliTests.Run(args);

    private const string Contradiction = CliTests.Contradiction;

    [Fact]
    public void Format_RelativePathWithDotSegments()
    {
        File(".cursor/rules/style.mdc", "---\ndescription: x\nalwaysApply: true\n---\n- Use tabs.\n");
        var previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = dir;
            Assert.Equal("CursorMdc", Json(Run(["lint", "./.cursor/rules/style.mdc", "--json"]))["detectedFormat"]!.GetValue<string>());
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
    }

    [Fact]
    public void DoubleDash_EndsOptions()
    {
        var path = File("--json", Contradiction);
        var previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = dir;
            var result = Run(["lint", "--", "--json"]);
            Assert.Equal(CliApp.Ok, result.Exit);
            Assert.StartsWith("--json: ", result.Out);
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
    }

}

[CollectionDefinition(nameof(WorkingDirectoryCollection), DisableParallelization = true)]
public sealed class WorkingDirectoryCollection;

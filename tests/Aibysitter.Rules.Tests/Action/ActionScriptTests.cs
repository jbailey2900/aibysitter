using System.Diagnostics;
using System.Text.Json.Nodes;
using Aibysitter.Rules.Tests.Parity;
using Xunit.Abstractions;

namespace Aibysitter.Rules.Tests.Action;

/// <summary>
/// action/lint.sh and action/post.sh under bash, with the built CLI and a stub gh.
/// Needs bash, git and jq: skipped on Windows or when missing locally, required in CI.
/// </summary>
public sealed class ActionScriptTests(ITestOutputHelper output) : IDisposable
{
    private const string Clean = "# Rules\n- Use tabs for indentation.\n";
    private const string Contradiction = "# Rules\n- Always use tabs.\n- Never use tabs.\n";

    private readonly string root = Directory.CreateTempSubdirectory("aibysitter-action-").FullName;

    private string Repo => Path.Combine(root, "repo");

    private string Out => Path.Combine(root, "out");

    private static string ActionDir => Path.Combine(NodeRunner.RepoRoot, "action");

    public void Dispose() => Directory.Delete(root, recursive: true);

    private bool ToolsOrSkip()
    {
        var missing = new[] { "bash", "git", "jq" }.Where(t => !OnPath(t)).ToList();
        if (!OperatingSystem.IsWindows() && missing.Count == 0)
        {
            return true;
        }

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")) && !OperatingSystem.IsWindows())
        {
            Assert.Fail($"Action script tests need {string.Join(", ", missing)} on PATH in CI.");
        }

        output.WriteLine("SKIPPED: action script tests need bash, git and jq on a non-Windows host.");
        return false;
    }

    private static bool OnPath(string tool) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Any(d => File.Exists(Path.Combine(d, tool)));

    private void RepoWith(params (string Path, string Content)[] files)
    {
        Directory.CreateDirectory(Repo);
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(Repo, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        Run("git", Repo, new Dictionary<string, string>(), "init", "-q");
        Run("git", Repo, new Dictionary<string, string>(), "add", "-A");
    }

    private static (int Exit, string Out) Run(string file, string workingDirectory, IDictionary<string, string> env, params string[] args)
    {
        var start = new ProcessStartInfo(file) { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        foreach (var (key, value) in env)
        {
            start.Environment[key] = value;
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(TimeSpan.FromMinutes(2)), $"{file} did not exit");
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    private (Dictionary<string, string> Outputs, JsonObject Report, string Summary, string Log) Lint(string files = "", bool failOnError = false, string failBelow = "", string? outDir = null)
    {
        var outputs = Path.Combine(root, "github-output");
        var summary = Path.Combine(root, "summary.md");
        File.WriteAllText(outputs, "");
        File.WriteAllText(summary, "");
        var (exit, log) = Run("bash", Repo, new Dictionary<string, string>
        {
            ["INPUT_FILES"] = files,
            ["INPUT_FAIL_ON_ERROR"] = failOnError ? "true" : "false",
            ["INPUT_FAIL_BELOW"] = failBelow,
            ["AIBYSITTER_CLI"] = "dotnet " + Path.Combine(AppContext.BaseDirectory, "Aibysitter.Cli.dll"),
            ["AIBYSITTER_OUT"] = outDir ?? Out,
            ["GITHUB_OUTPUT"] = outputs,
            ["GITHUB_STEP_SUMMARY"] = summary,
            ["GITHUB_WORKSPACE"] = Repo,
        }, Path.Combine(ActionDir, "lint.sh"));

        Assert.True(exit == 0, log);
        var values = File.ReadAllLines(outputs).Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        return (values, JsonNode.Parse(File.ReadAllText(values["report"]))!.AsObject(), File.ReadAllText(summary), log);
    }

    private static List<string> Files(JsonObject report) => report["files"]!.AsArray().Select(f => f!["file"]!.GetValue<string>()).ToList();

    [Fact]
    public void Discovery_MatchesRulesFormatsFromFileName()
    {
        if (!ToolsOrSkip())
        {
            return;
        }

        string[] candidates =
        [
            "CLAUDE.md", "AGENTS.md", "GEMINI.md", "sub/dir/CLAUDE.md", "pkg/AGENTS.md", "claude.md", "CLAUDE.md.bak",
            ".cursorrules", "docs/.cursorrules", ".windsurfrules", "a/.windsurfrules",
            ".github/copilot-instructions.md", "docs/copilot-instructions.md", "x/.github/copilot-instructions.md",
            ".cursor/rules/style.mdc", ".cursor/rules/nested/deep/style.mdc", "app/.cursor/rules/x.mdc", ".cursor/rules/-bad.mdc",
            ".cursor/style.mdc", "README.md", "notes.mdc",
        ];
        RepoWith([.. candidates.Select(c => (c, Clean))]);

        var (_, report, _, _) = Lint();

        var expected = candidates.Where(c => RulesFormats.FromFileName(c) is not null).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(expected, Files(report).Order(StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void Globs_AndPaths_Deduplicated_OutputsAndSummary()
    {
        if (!ToolsOrSkip())
        {
            return;
        }

        RepoWith(("clean/CLAUDE.md", Clean), ("errors/CLAUDE.md", Contradiction), ("other/notes.md", Clean));

        var (outputs, report, summary, _) = Lint("errors/*.md  clean/**/*.md\n./clean/CLAUDE.md zzz/*.md");

        Assert.Equal(["errors/CLAUDE.md", "clean/CLAUDE.md"], Files(report));
        Assert.Equal("ok", outputs["status"]);
        Assert.Equal("A", outputs["grade"]);
        Assert.Equal("90", outputs["score"]);
        Assert.Equal("1", outputs["findings"]);
        Assert.Contains("| `errors/CLAUDE.md` | 90 | A | 1 |", summary);
        Assert.Contains("| `errors/CLAUDE.md:3` | Error | R003 |", summary);
    }

    [Theory]
    [InlineData("docs/**/CLAUDE.md", "docs/CLAUDE.md,docs/a/b/CLAUDE.md")]
    [InlineData("docs/*.md", "docs/CLAUDE.md")]
    [InlineData("**/CLAUDE.md", "docs/CLAUDE.md,docs/a/b/CLAUDE.md,other/CLAUDE.md")]
    [InlineData("d[o]cs/?LAUDE.md", "docs/CLAUDE.md")]
    [InlineData("[!d]*/CLAUDE.md", "other/CLAUDE.md")]
    [InlineData("ignored/*.md", "")]
    [InlineData("ignored/CLAUDE.md", "ignored/CLAUDE.md")]
    public void Globs_MatchFilesGitTracksOrWouldTrack(string files, string expected)
    {
        if (!ToolsOrSkip())
        {
            return;
        }

        RepoWith(
            (".gitignore", "ignored/\n"),
            ("docs/CLAUDE.md", Clean),
            ("docs/a/b/CLAUDE.md", Clean),
            ("other/CLAUDE.md", Clean),
            ("ignored/CLAUDE.md", Clean));

        var (_, report, _, _) = Lint(files);

        Assert.Equal(expected.Split(',', StringSplitOptions.RemoveEmptyEntries), Files(report));
    }

    [Fact]
    public void OutputFolderWithBackslash_ReportStillMerged()
    {
        if (!ToolsOrSkip())
        {
            return;
        }

        RepoWith(("CLAUDE.md", Contradiction));

        var (outputs, report, _, _) = Lint("CLAUDE.md", outDir: Path.Combine(root, "out\\a_temp"));

        Assert.Equal(["CLAUDE.md"], Files(report));
        Assert.Equal("1", outputs["findings"]);
    }

    [Theory]
    [InlineData("lint.sh")]
    [InlineData("post.sh")]
    public void Scripts_RunOnBash32(string script)
    {
        var text = File.ReadAllText(Path.Combine(ActionDir, script));

        foreach (var feature in new[] { "mapfile", "readarray", "globstar", "declare -A", "local -n", ",,}", "^^}", "&>>", "|&" })
        {
            Assert.DoesNotContain(feature, text, StringComparison.Ordinal);
        }

        Assert.DoesNotMatch(@"(?<!\+)""\$\{[a-z_]+\[@\]\}""", text.Replace("\"${cli[@]}\"", "", StringComparison.Ordinal));
        Assert.Contains("jq() { command jq \"$@\" | tr -d '\\r'; }", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, "", "threshold")]
    [InlineData(false, "A", "ok")]
    [InlineData(false, "", "ok")]
    public void Thresholds_SetStatus(bool failOnError, string failBelow, string status)
    {
        if (!ToolsOrSkip())
        {
            return;
        }

        RepoWith(("CLAUDE.md", Contradiction));

        Assert.Equal(status, Lint("CLAUDE.md", failOnError, failBelow).Outputs["status"]);
    }

    [Fact]
    public void ErrorAnnotation_EscapesPercent()
    {
        if (!ToolsOrSkip())
        {
            return;
        }

        RepoWith(("CLAUDE.md", Clean));

        var (_, _, _, log) = Lint("CLAUDE.md nope%0A/AGENTS.md");

        Assert.Contains("::error title=aibysitter::nope%250A/AGENTS.md: aibysitter: cannot read nope%250A/AGENTS.md: not found", log);
    }

    [Fact]
    public void MissingFile_ErrorStatus_ErrorAnnotation()
    {
        if (!ToolsOrSkip())
        {
            return;
        }

        RepoWith(("CLAUDE.md", Clean));

        var (outputs, report, summary, log) = Lint("CLAUDE.md nope/AGENTS.md");

        Assert.Equal("error", outputs["status"]);
        Assert.Equal(["CLAUDE.md"], Files(report));
        Assert.Contains("::error title=aibysitter::nope/AGENTS.md: aibysitter: cannot read nope/AGENTS.md: not found", log);
        Assert.Contains("Error: nope/AGENTS.md", summary);
    }

    [Fact]
    public void RepeatedRuns_InOneJob_KeepSeparateReports()
    {
        if (!ToolsOrSkip())
        {
            return;
        }

        RepoWith(("clean/CLAUDE.md", Clean), ("errors/CLAUDE.md", Contradiction), ("warn/AGENTS.md", "# Rules\n- Handle errors properly.\n"));

        var first = Lint();
        var second = Lint("errors/CLAUDE.md");
        var third = Lint("nothing/*.md");

        Assert.Equal(3, Files(first.Report).Count);
        Assert.Equal(["errors/CLAUDE.md"], Files(second.Report));
        Assert.Equal("1", second.Outputs["findings"]);
        Assert.Empty(Files(third.Report));
        Assert.NotEqual(first.Outputs["report"], second.Outputs["report"]);
        Assert.Equal(3, Files(JsonNode.Parse(File.ReadAllText(first.Outputs["report"]))!.AsObject()).Count);
    }

    [Fact]
    public void NoFiles_OkAndSaysSo()
    {
        if (!ToolsOrSkip())
        {
            return;
        }

        RepoWith(("README.md", "# readme\n"));

        var (outputs, report, summary, _) = Lint();

        Assert.Equal("ok", outputs["status"]);
        Assert.Equal("0", outputs["findings"]);
        Assert.Empty(Files(report));
        Assert.Contains("No rules files found.", summary);
    }

    private (string Log, List<JsonObject> Bodies, List<string> Calls) Post(string reportPath, bool failGh, string? eventJson = null)
    {
        var stub = Path.Combine(root, "stub");
        var calls = Path.Combine(root, "calls");
        Directory.CreateDirectory(stub);
        Directory.CreateDirectory(calls);
        var gh = Path.Combine(stub, "gh");
        File.WriteAllText(gh, """
            #!/usr/bin/env bash
            n=$(ls "$GH_STUB_DIR" | wc -l)
            printf '%s\n' "$*" > "$GH_STUB_DIR/$n.args"
            cat > "$GH_STUB_DIR/$n.body"
            if [ "$GH_STUB_FAIL" = 1 ]; then echo "HTTP 403: Resource not accessible by integration" >&2; exit 1; fi
            echo '{"id": 4242}'
            """.Replace("\r\n", "\n"));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(gh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var eventPath = Path.Combine(root, "event.json");
        File.WriteAllText(eventPath, eventJson ?? "{}");

        var (exit, log) = Run("bash", Repo, new Dictionary<string, string>
        {
            ["PATH"] = stub + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
            ["GH_STUB_DIR"] = calls,
            ["GH_STUB_FAIL"] = failGh ? "1" : "0",
            ["REPORT"] = reportPath,
            ["GITHUB_REPOSITORY"] = "o/r",
            ["GITHUB_SHA"] = "def456",
            ["GITHUB_EVENT_PATH"] = eventPath,
            ["GITHUB_OUTPUT"] = Path.Combine(root, "post-output"),
        }, Path.Combine(ActionDir, "post.sh"));

        Assert.True(exit == 0, log);
        var bodies = Directory.GetFiles(calls, "*.body").OrderBy(f => int.Parse(Path.GetFileNameWithoutExtension(f)))
            .Select(f => JsonNode.Parse(File.ReadAllText(f))!.AsObject()).ToList();
        var args = Directory.GetFiles(calls, "*.args").OrderBy(f => int.Parse(Path.GetFileNameWithoutExtension(f))).Select(f => File.ReadAllText(f).Trim()).ToList();
        return (log, bodies, args);
    }

    [Fact]
    public void Post_CheckRun_PrHeadSha_Batches50_Neutral()
    {
        if (!ToolsOrSkip())
        {
            return;
        }

        var big = "# Rules\n" + string.Concat(Enumerable.Range(0, 60).Select(i => $"- Always use tool{i}.\n- Never use tool{i}.\n"));
        RepoWith(("CLAUDE.md", big), ("AGENTS.md", "# Rules\n- Handle errors properly.\n"));
        var (outputs, _, _, _) = Lint();

        var (log, bodies, calls) = Post(outputs["report"], failGh: false, """{"pull_request":{"head":{"sha":"abc123"}}}""");

        Assert.Equal(["api --method POST repos/o/r/check-runs --input -", "api --method PATCH repos/o/r/check-runs/4242 --input -"], calls);
        Assert.Equal("Aibysitter rules", bodies[0]["name"]!.GetValue<string>());
        Assert.Equal("abc123", bodies[0]["head_sha"]!.GetValue<string>());
        Assert.Equal("completed", bodies[0]["status"]!.GetValue<string>());
        Assert.Equal("neutral", bodies[0]["conclusion"]!.GetValue<string>());
        Assert.Equal("61 findings in 2 files", bodies[0]["output"]!["title"]!.GetValue<string>());
        Assert.Equal(50, bodies[0]["output"]!["annotations"]!.AsArray().Count);
        Assert.Equal(11, bodies[1]["output"]!["annotations"]!.AsArray().Count);
        var warning = bodies.SelectMany(b => b["output"]!["annotations"]!.AsArray()).Single(a => a!["path"]!.GetValue<string>() == "AGENTS.md")!;
        Assert.Equal("warning", warning["annotation_level"]!.GetValue<string>());
        Assert.Equal("R002 (Warning)", warning["title"]!.GetValue<string>());
        Assert.EndsWith("\nFix: Name the concrete action, file, or command.", warning["message"]!.GetValue<string>());
        Assert.Contains("Check run 4242: neutral, 61 annotations.", log);
    }

    [Theory]
    [InlineData("# Rules\n- Use tabs.\n", false, "success")]
    [InlineData(Contradiction, true, "failure")]
    public void Post_Conclusion_SuccessAndFailure_UsesGithubShaOutsidePr(string content, bool failOnError, string conclusion)
    {
        if (!ToolsOrSkip())
        {
            return;
        }

        RepoWith(("CLAUDE.md", content));
        var (outputs, _, _, _) = Lint(failOnError: failOnError);

        var (_, bodies, calls) = Post(outputs["report"], failGh: false);

        Assert.Single(calls);
        Assert.Equal(conclusion, bodies[0]["conclusion"]!.GetValue<string>());
        Assert.Equal("def456", bodies[0]["head_sha"]!.GetValue<string>());
    }

    [Fact]
    public void Post_NoFiles_SuccessCheckRun()
    {
        if (!ToolsOrSkip())
        {
            return;
        }

        RepoWith(("README.md", "# readme\n"));
        var (outputs, _, _, _) = Lint();

        var (_, bodies, _) = Post(outputs["report"], failGh: false);

        Assert.Equal("success", bodies[0]["conclusion"]!.GetValue<string>());
        Assert.Equal("No rules files found", bodies[0]["output"]!["title"]!.GetValue<string>());
    }

    [Fact]
    public void Post_TokenCannotWriteChecks_FallsBackToWorkflowCommands()
    {
        if (!ToolsOrSkip())
        {
            return;
        }

        RepoWith(("dir,x/CLAUDE.md", Contradiction + "- Handle errors properly.\n"));
        var (outputs, _, _, _) = Lint();

        var (log, _, calls) = Post(outputs["report"], failGh: true);

        Assert.Single(calls);
        Assert.Contains("Check run not created (HTTP 403: Resource not accessible by integration", log);
        Assert.Contains("::error file=dir%2Cx/CLAUDE.md,line=3,title=R003 (Error)::Contradicts line 2: \"use tabs\" is both required and forbidden.%0AFix: Keep one instruction and delete the other.", log);
        Assert.Contains("::warning file=dir%2Cx/CLAUDE.md,line=4,title=R002 (Warning)::", log);
    }
}

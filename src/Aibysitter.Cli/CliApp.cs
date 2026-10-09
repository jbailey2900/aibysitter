using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aibysitter.Rules;

namespace Aibysitter.Cli;

/// <summary><c>aibysitter</c> entry point. Offline; uses <see cref="LintEngine"/> and the embedded packs directly.</summary>
public static class CliApp
{
    public const int Ok = 0;
    public const int ThresholdFailed = 1;
    public const int UsageError = 2;
    public const int FileError = 3;
    public const int TimedOut = 4;

    /// <summary>Engine for <c>lint</c>; replaced in tests.</summary>
    internal static Func<LintEngine> EngineFactory { get; set; } = () => new LintEngine();

    public const string StdinLabel = "<stdin>";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Version
    {
        get
        {
            var v = typeof(CliApp).Assembly.GetName().Version!;
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public static string VersionLine => $"aibysitter {Version} (ruleset v{RulesetVersion.Current})";

    public static string Usage =>
        $"""
        {VersionLine}
        Lints a rules file for AI coding agents. Same rules and scoring as aibysitting.net. Runs offline.

        Usage:
          aibysitter lint <file|-> [options]
          aibysitter fix <file|-> [--dry-run] [--disable <ids>] [--format <name>] [--stdin-path <path>]
          aibysitter init --packs <ids> --format <name> [--title <text>] [--output <path>] [--force]
          aibysitter packs
          aibysitter hook claude-code [--disable <ids>]
          aibysitter --version
          aibysitter --help

        lint options:
          --format <name>       {string.Join(", ", Enum.GetNames<RulesFormat>())}.
                                Default: from the file path, else detected from content.
          --disable <ids>       Rule IDs to skip, comma-separated, for example R002,R005.
          --json                JSON output, same fields as the /api/lint response plus "file".
          --fail-on-error       Exit 1 when any Error finding remains.
          --fail-below <grade>  Exit 1 when the grade is below A, B, C or D.
          --stdin-path <path>   With -, names standard input: the format comes from this path and the report uses it.

        fix: fixes R011 (deletes empty headings) and R012 (closes the fence at end of file), repeated until none
          apply. Writes the file in place. With -, writes the fixed text to standard output.
          --dry-run             Print a unified diff instead; exit 1 when there are changes.

        init: writes a rules file composed from packs, then prints its score.
          --packs <ids>         Pack IDs, comma-separated, in order. 'aibysitter packs' lists them.
          --format <name>       claude, agents, gemini, copilot, cursor, cursorrules, windsurf.
          --title <text>        H1 of the file. Default: Project rules.
          --output <path>       Default: where the format goes, for example CLAUDE.md or .cursor/rules/<pack>.mdc.
          --force               Overwrite an existing file.

        hook claude-code: Claude Code PostToolUse hook. Reads the hook JSON on standard input; when the edited
          file is a rules file with Error or Warning findings, prints them to standard error and exits 2 so Claude
          fixes them. Otherwise exits 0. Extra flags also come from AIBYSITTER_HOOK_ARGS (--disable only).

        Exit codes: 0 ok, 1 threshold failed (lint) or changes found (fix --dry-run), 2 usage error, 3 file not readable or not writable, 4 lint timed out.
        """;

    public static int Run(string[] args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        try
        {
            return Dispatch(args, stdin, stdout, stderr);
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            stderr.WriteLine($"aibysitter: {RegexTimeout.LintMessage}");
            return TimedOut;
        }
    }

    private static int Dispatch(string[] args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        switch (args)
        {
            case []:
                stderr.WriteLine(Usage);
                return UsageError;
            case ["--help" or "-h" or "help"]:
                stdout.WriteLine(Usage);
                return Ok;
            case ["--version"]:
                stdout.WriteLine(VersionLine);
                return Ok;
            case ["packs", .. var packsArgs]:
                return InitCommand.Packs(packsArgs, stdout, m => UsageFail(stderr, m));
            case ["init", .. var initArgs]:
                return InitCommand.Init(initArgs, stdout, stderr, m => UsageFail(stderr, m));
            case ["fix", .. var fixArgs]:
                return FixCommand.Run(fixArgs, stdin, stdout, stderr, m => UsageFail(stderr, m));
            case ["hook", "claude-code", .. var hookArgs]:
                return HookCommand.ClaudeCode(hookArgs, stdin, stderr);
            case ["hook", ..]:
                return UsageFail(stderr, "hook needs a name: claude-code.");
            case ["lint", .. var rest]:
                var (options, error) = LintOptions.Parse(rest);
                return options is null ? UsageFail(stderr, error!) : Lint(options, stdin, stdout, stderr);
            default:
                return UsageFail(stderr, $"Unknown command \"{args[0]}\".");
        }
    }

    private static int UsageFail(TextWriter stderr, string message)
    {
        stderr.WriteLine($"aibysitter: {message}");
        stderr.WriteLine("Run 'aibysitter --help' for usage.");
        return UsageError;
    }

    private static int Lint(LintOptions options, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var engine = EngineFactory();
        if (!LintReport.TryNormalizeDisabled(engine, options.Disable, out var disabled, out var unknown))
        {
            return UsageFail(stderr, $"Not a lint rule ID: {string.Join(", ", unknown)}.");
        }

        var isStdin = options.Path == LintOptions.Stdin;
        string text;
        try
        {
            text = isStdin ? stdin.ReadToEnd() : ReadFile(options.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"aibysitter: cannot read {options.Path}: {Reason(ex, options.Path)}");
            return FileError;
        }

        var label = isStdin ? options.StdinPath ?? StdinLabel : options.Path;
        var format = options.Format ?? (isStdin && options.StdinPath is null ? null : PathFormat.Resolve(label)) ?? RulesFormat.Auto;
        var report = LintReport.Create(engine, text.TrimStart('﻿'), format, disabled);

        stdout.Write(options.Json ? ToJson(label, report) : TextReport.Write(label, report));
        return CheckThresholds(options, report, stderr);
    }

    private static string ReadFile(string path)
    {
        if (Directory.Exists(path))
        {
            throw new IOException("is a directory");
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(File.ReadAllBytes(path));
    }

    private static string Reason(Exception ex, string path) => ex switch
    {
        FileNotFoundException or DirectoryNotFoundException => "not found",
        UnauthorizedAccessException => "access denied",
        _ when ex.Message == "is a directory" => "is a directory",
        _ => ex.Message.Replace(Path.GetFullPath(path), path, StringComparison.Ordinal),
    };

    private static string ToJson(string label, LintReport report)
    {
        var body = JsonSerializer.SerializeToNode(report, Json)!.AsObject();
        var output = new JsonObject { ["file"] = label };
        foreach (var (key, value) in body.ToList())
        {
            body.Remove(key);
            output[key] = value;
        }

        return output.ToJsonString(Json) + "\n";
    }

    private static int CheckThresholds(LintOptions options, LintReport report, TextWriter stderr)
    {
        var failures = new List<string>();
        var errors = report.Findings.Count(f => f.Severity == nameof(Severity.Error));
        if (options.FailOnError && errors > 0)
        {
            failures.Add($"{errors} Error finding{(errors == 1 ? "" : "s")}");
        }

        if (options.FailBelow is { } threshold && report.Grade[0] > threshold)
        {
            failures.Add($"grade {report.Grade} is below {threshold}");
        }

        if (failures.Count == 0)
        {
            return Ok;
        }

        stderr.WriteLine($"aibysitter: failed: {string.Join("; ", failures)}.");
        return ThresholdFailed;
    }
}

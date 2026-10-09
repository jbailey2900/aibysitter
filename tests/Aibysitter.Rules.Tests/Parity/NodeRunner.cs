using System.Diagnostics;
using Xunit.Abstractions;

namespace Aibysitter.Rules.Tests.Parity;

/// <summary>Runs parity-runner.mjs under Node. Node missing: fails when CI is set, otherwise the caller skips.</summary>
internal static class NodeRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public static string RepoRoot { get; } = FindRepoRoot();

    public static string JsDirectory => Path.Combine(RepoRoot, "src", "Aibysitter.Web", "wwwroot", "js");

    private static string Script => Path.Combine(RepoRoot, "tests", "Aibysitter.Rules.Tests", "Parity", "parity-runner.mjs");

    /// <summary>Path to node, or null after reporting why the test is skipped. Throws in CI.</summary>
    public static string? FindNodeOrSkip(ITestOutputHelper output)
    {
        var node = FindOnPath(OperatingSystem.IsWindows() ? "node.exe" : "node");
        if (node is not null)
        {
            return node;
        }

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")))
        {
            Assert.Fail("Node.js is required for the browser parity tests in CI and was not found on PATH.");
        }

        output.WriteLine("SKIPPED: Node.js not found on PATH; browser parity not checked.");
        return null;
    }

    /// <summary>Best-of-three milliseconds per (pattern key, text), matched with the exported JS pattern.</summary>
    public static IReadOnlyList<double> TimePatterns(string node, IReadOnlyList<(string Key, string Text)> items) =>
        System.Text.Json.JsonSerializer.Deserialize<double[]>(Run(node, "time-patterns", System.Text.Json.JsonSerializer.Serialize(items.Select(i => new { key = i.Key, text = i.Text }))))!;

    /// <summary>Best-of-three milliseconds per text, linted by the browser engine.</summary>
    public static IReadOnlyList<double> TimeLint(string node, IReadOnlyList<string> texts) =>
        System.Text.Json.JsonSerializer.Deserialize<double[]>(Run(node, "time-lint", System.Text.Json.JsonSerializer.Serialize(texts)))!;

    public static string Run(string node, string mode, string stdin)
    {
        var start = new ProcessStartInfo(node)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            ArgumentList = { Script, JsDirectory, mode },
        };

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start node.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.StandardInput.Write(stdin);
        process.StandardInput.Close();

        if (!process.WaitForExit(Timeout))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"parity-runner.mjs did not finish within {Timeout}.");
        }

        Assert.True(process.ExitCode == 0, $"parity-runner.mjs exited {process.ExitCode}: {stderr.Result}");
        return stdout.Result;
    }

    private static string? FindOnPath(string fileName) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, fileName))
            .FirstOrDefault(File.Exists);

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Aibysitter.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Aibysitter.slnx not found above " + AppContext.BaseDirectory);
    }
}

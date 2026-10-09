using System.Text.RegularExpressions;

namespace Aibysitter.Rules.Repo;

public enum ReferenceKind
{
    Path,
    PackageScript,
    MakeTarget,
    MsBuildTarget,
}

/// <param name="Name">Path as written, or the script / target name.</param>
public sealed record RepoReference(int Line, ReferenceKind Kind, string Name);

// Path rules: inline code and link targets may name directories; bare prose paths need a known file extension.

/// <summary>
/// Paths and commands a rules file names. Paths: inline code, bare relative paths, and relative link targets in prose
/// and headings. Commands (npm / pnpm / yarn / bun scripts, make targets, MSBuild -t: targets, dotnet project paths):
/// prose inline code and code-block lines. Placeholders, URLs, absolute paths, globs, and generated folders are skipped.
/// </summary>
public static partial class RepoReferences
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "md", "mdc", "mdx", "txt", "json", "jsonc", "yml", "yaml", "toml", "ini", "xml", "config", "props", "targets",
        "cs", "csx", "csproj", "fsproj", "vbproj", "sln", "slnx", "razor", "cshtml",
        "ts", "tsx", "js", "jsx", "mjs", "cjs", "vue", "svelte", "astro", "css", "scss", "html",
        "py", "pyi", "go", "mod", "rs", "rb", "java", "kt", "kts", "swift", "php", "c", "h", "cpp", "hpp",
        "sh", "ps1", "psm1", "bat", "cmd", "sql", "proto", "graphql", "prisma", "lock", "gradle", "tf",
    };

    private static readonly HashSet<string> ExactNames = new(StringComparer.Ordinal)
    {
        "Dockerfile", "Makefile", "makefile", "GNUmakefile", "Justfile", "justfile", "Procfile", "Gemfile", "Rakefile", "Jenkinsfile",
    };

    private static readonly HashSet<string> GeneratedSegments = new(StringComparer.Ordinal)
    {
        "node_modules", "bin", "obj", "dist", "build", "out", "target", ".venv", "venv", "__pycache__", ".next", ".nuxt", "coverage", ".turbo",
    };

    /// <summary>Generic example names that do not refer to a real file.</summary>
    private static readonly HashSet<string> ExampleStems = new(StringComparer.OrdinalIgnoreCase)
    {
        "foo", "bar", "baz", "file", "example", "sample", "my-file", "myfile", "name", "filename", "path", "your-file", "x", "y", "a", "b",
    };

    /// <summary>pnpm / yarn / bun subcommands that are not package scripts.</summary>
    private static readonly HashSet<string> RunnerBuiltins = new(StringComparer.Ordinal)
    {
        "install", "i", "add", "remove", "rm", "uninstall", "un", "update", "up", "upgrade", "upgrade-interactive", "exec", "dlx", "x", "create",
        "init", "link", "unlink", "publish", "pack", "audit", "outdated", "why", "list", "ls", "info", "view", "config", "cache", "store", "dedupe",
        "prune", "rebuild", "import", "patch", "patch-commit", "env", "setup", "self-update", "help", "version", "login", "logout", "whoami",
        "workspace", "workspaces", "global", "bin", "root", "restart", "stop", "ci", "run", "node", "doctor", "licenses",
        "fetch", "deploy", "approve-builds", "set", "plugin", "constraints", "install-test", "it", "pm",
    };

    private static readonly HashSet<string> MsBuildBuiltins = new(StringComparer.OrdinalIgnoreCase)
    {
        "Build", "Rebuild", "Clean", "Restore", "Publish", "Pack", "Test", "VSTest", "Run", "Compile", "ResolveReferences", "GenerateNuspec",
    };

    public static IReadOnlyList<RepoReference> Extract(RulesFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var refs = new List<RepoReference>();
        foreach (var line in file.Lines.Where(l => !l.IsBlank && !l.IsFrontmatter && !l.IsDirective))
        {
            var exampleLine = ExampleLineRegex().IsMatch(line.Text);
            if (line.IsInCodeFence)
            {
                if (!FenceRegex().IsMatch(line.Text))
                {
                    AddCommands(line.Number, StripPrompt(line.Text), refs);
                }

                continue;
            }

            var text = line.Text;
            foreach (Match span in CodeSpanRegex().Matches(text))
            {
                var code = TrailingCommentRegex().Replace(span.Groups[1].Value, string.Empty).Trim();
                if (!AddCommands(line.Number, code, refs) && !exampleLine && IsPathLike(code))
                {
                    refs.Add(new RepoReference(line.Number, ReferenceKind.Path, code));
                }
            }

            var outside = CodeSpanRegex().Replace(text, " ");
            foreach (Match link in LinkTargetRegex().Matches(outside))
            {
                var target = link.Groups[1].Value.Split('#')[0].Trim();
                if (target.Length > 0 && !exampleLine && IsPathLike(target, requireSlashOrExtension: false))
                {
                    refs.Add(new RepoReference(line.Number, ReferenceKind.Path, target));
                }
            }

            outside = LinkRegex().Replace(UrlRegex().Replace(outside, " "), "$1");
            foreach (Match bare in BarePathRegex().Matches(outside))
            {
                var path = bare.Value.TrimEnd('.', ',', ';', ':', ')');
                if (!exampleLine && path.Contains('/') && IsPathLike(path) && HasKnownExtension(path))
                {
                    refs.Add(new RepoReference(line.Number, ReferenceKind.Path, path));
                }
            }
        }

        return refs.Distinct().ToList();
    }

    /// <summary>True when any reference needs a manifest of this kind.</summary>
    public static bool Needs(IEnumerable<RepoReference> refs, ReferenceKind kind) => refs.Any(r => r.Kind == kind);

    private static string StripPrompt(string text) => TrailingCommentRegex().Replace(PromptRegex().Replace(text, string.Empty), string.Empty);

    /// <summary>Adds command references found in <paramref name="text"/>. Returns true when the text is a command.</summary>
    private static bool AddCommands(int line, string text, List<RepoReference> refs)
    {
        var any = false;
        foreach (var segment in CommandSplitRegex().Split(text))
        {
            var cmd = segment.Trim();
            if (cmd.Length == 0)
            {
                continue;
            }

            if (cmd.StartsWith("cd ", StringComparison.Ordinal))
            {
                return true;
            }

            var pkg = PackageCommandRegex().Match(cmd);
            if (pkg.Success)
            {
                any = true;
                var runner = pkg.Groups["runner"].Value;
                var hasRun = pkg.Groups["run"].Success;
                var name = pkg.Groups["name"].Value;
                var scoped = ScopeFlagRegex().IsMatch(cmd);
                // npm without "run": only "npm test" resolves to a script. pnpm / yarn / bun without "run" may run an
                // installed binary (pnpm vitest), so only names with ':' (build:watch) count as scripts.
                var isScript = hasRun
                    || (runner == "npm" && name is "test" or "t" or "tst")
                    || (runner != "npm" && name.Contains(':') && !RunnerBuiltins.Contains(name));
                if (!scoped && name.Length > 0 && !name.StartsWith('-') && !name.Contains('/') && !HasKnownExtension(name) && isScript)
                {
                    refs.Add(new RepoReference(line, ReferenceKind.PackageScript, name is "t" or "tst" ? "test" : name));
                }

                continue;
            }

            var make = MakeCommandRegex().Match(cmd);
            if (make.Success)
            {
                any = true;
                if (!MakeDirFlagRegex().IsMatch(cmd))
                {
                    foreach (var target in make.Groups["targets"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).TakeWhile(t => !t.StartsWith('-') && !t.Contains('=') && !PlaceholderRegex().IsMatch(t)))
                    {
                        refs.Add(new RepoReference(line, ReferenceKind.MakeTarget, target));
                    }
                }

                continue;
            }

            if (DotnetRegex().IsMatch(cmd))
            {
                any = true;
                foreach (Match t in MsBuildTargetRegex().Matches(cmd))
                {
                    foreach (var name in t.Groups["t"].Value.Split(';', ',').Where(n => n.Length > 0 && !MsBuildBuiltins.Contains(n)))
                    {
                        refs.Add(new RepoReference(line, ReferenceKind.MsBuildTarget, name));
                    }
                }

                var project = DotnetProjectRegex().Match(cmd);
                if (project.Success && IsPathLike(project.Groups["p"].Value, requireSlashOrExtension: false))
                {
                    refs.Add(new RepoReference(line, ReferenceKind.Path, project.Groups["p"].Value));
                }
            }
        }

        return any;
    }

    private static bool HasKnownExtension(string path)
    {
        var name = path.TrimEnd('/')[(path.TrimEnd('/').LastIndexOf('/') + 1)..];
        var dot = name.LastIndexOf('.');
        return dot > 0 && Extensions.Contains(name[(dot + 1)..]);
    }

    private static bool IsExampleName(string segment)
    {
        var dot = segment.LastIndexOf('.');
        var stem = dot > 0 ? segment[..dot] : segment;
        return ExampleStems.Contains(stem) || stem.StartsWith("my-", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("new-", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("your-", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPathLike(string text, bool requireSlashOrExtension = true)
    {
        if (text.Length is 0 or > 200 || !PathCharsRegex().IsMatch(text) || PlaceholderRegex().IsMatch(text))
        {
            return false;
        }

        if (text.StartsWith('/') || text.StartsWith('~') || text.StartsWith('@') || text.Contains("://") || text.Contains('*') || text.Contains("..."))
        {
            return false;
        }

        var normalized = RepoSnapshot.Normalize(text);

        // Single-segment names (MyService.ts, feature/) are examples or module-relative; only multi-segment paths are checked.
        if (!normalized.Contains('/'))
        {
            return false;
        }

        if (normalized.Length == 0 || normalized.Split('/').Any(s => GeneratedSegments.Contains(s) || IsExampleName(s)))
        {
            return false;
        }

        var name = normalized[(normalized.LastIndexOf('/') + 1)..];
        if (name.StartsWith(".env", StringComparison.Ordinal))
        {
            return false;
        }

        // "AGENTS.md/README.md" is an "or" list, not a path.
        if (normalized.Split('/')[..^1].Any(HasKnownExtension))
        {
            return false;
        }

        var dot = name.LastIndexOf('.');
        var hasExtension = dot > 0 && Extensions.Contains(name[(dot + 1)..]);

        // A dotted last segment with an unknown extension is a code symbol (pkg/agent.BuildEnv), not a file.
        if (dot > 0 && !hasExtension && !name.StartsWith('.'))
        {
            return false;
        }

        if (VersionRegex().IsMatch(name))
        {
            return false;
        }

        return !requireSlashOrExtension || text.Contains('/') || hasExtension || ExactNames.Contains(name);
    }

    [GeneratedRegex(@"^\s{0,3}(`{3,}|~{3,})")]
    private static partial Regex FenceRegex();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex CodeSpanRegex();

    [GeneratedRegex(@"\]\(\s*(?!https?:|mailto:|#)([^)\s\]]+)\s*\)")]
    private static partial Regex LinkTargetRegex();

    [GeneratedRegex(@"\[([^\[\]]*)\]\([^)\[]*\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"(?<![a-z0-9+.-])[a-z][a-z0-9+.-]*://\S+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    /// <summary>Relative paths with at least one slash, outside code spans.</summary>
    [GeneratedRegex(@"(?<![\w/.:@~$%<\[{-])(?:\.{1,2}/)?[A-Za-z0-9_.][\w.-]*(?:/[\w.-]+)+/?")]
    private static partial Regex BarePathRegex();

    [GeneratedRegex(@"^[\w./-]+$")]
    private static partial Regex PathCharsRegex();

    /// <summary>
    /// Lines that describe a file to create, give an example, forbid a path, or describe one as absent or generated.
    /// </summary>
    [GeneratedRegex(@"\be\.g\.|\bfor example\b|\bsuch as\b|\bcreate\b|\badd a new\b|\bnever\b|\bdo not\b|\bdon't\b|\bremoved\b|\bdeleted\b|\buntracked\b|\bgenerated\b|\bgitignored\b|\bnot committed\b|\bno longer\b|\bif (?:it )?exists\b|\bif present\b|\bwhen present\b", RegexOptions.IgnoreCase)]
    private static partial Regex ExampleLineRegex();

    [GeneratedRegex(@"path/to|your[-_]|<|>|\[|\]|\{|\}|::|\$|%|\bxxx|\bexample\b", RegexOptions.IgnoreCase)]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"^v?\d+(?:\.\d+)+$")]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"^\s*(?:\$|>|PS>|#)\s+")]
    private static partial Regex PromptRegex();

    [GeneratedRegex(@"\s+#.*$")]
    private static partial Regex TrailingCommentRegex();

    /// <summary>Shell separators. A bare ';' (no following space) is left alone: MSBuild uses it in -t:A;B.</summary>
    [GeneratedRegex(@"(?<!\s)\s*(?:&&|\|\||\|)\s*|;\s+")]
    private static partial Regex CommandSplitRegex();

    [GeneratedRegex(@"^(?<runner>npm|pnpm|yarn|bun)(?:\s+(?:--?[\w-]+(?:=\S+)?|-[A-Za-z]))*\s+(?:(?<run>run(?:-script)?)\s+)?(?<name>[\w:.@/-]+)")]
    private static partial Regex PackageCommandRegex();

    [GeneratedRegex(@"\s(?:--filter|-F|--workspace|-w|--prefix|--cwd|--dir|-C|--recursive|-r)\b")]
    private static partial Regex ScopeFlagRegex();

    [GeneratedRegex(@"^make(?<targets>(?:\s+\S+)*)\s*$")]
    private static partial Regex MakeCommandRegex();

    [GeneratedRegex(@"\s(?:-C|-f|--directory|--file)\b")]
    private static partial Regex MakeDirFlagRegex();

    [GeneratedRegex(@"^dotnet\s")]
    private static partial Regex DotnetRegex();

    [GeneratedRegex(@"(?:^|\s)(?:-t|/t|-target|/target|--target):(?<t>[\w;,.-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex MsBuildTargetRegex();

    [GeneratedRegex(@"(?:--project\s+|^dotnet\s+(?:test|build|run|publish|pack)\s+)(?<p>[^\s-][^\s]*\.(?:csproj|fsproj|vbproj|sln|slnx))(?:\s|$)")]
    private static partial Regex DotnetProjectRegex();
}

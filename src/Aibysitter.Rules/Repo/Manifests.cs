using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aibysitter.Rules.Repo;

/// <summary>Script and target names declared by package.json, Makefiles, and MSBuild files. Null when unreadable.</summary>
public static partial class Manifests
{
    public static readonly IReadOnlyList<string> MakefileNames = ["GNUmakefile", "makefile", "Makefile"];

    public static IReadOnlySet<string>? PackageScripts(string? json)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("scripts", out var scripts) && scripts.ValueKind == JsonValueKind.Object
                ? scripts.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Explicit targets. Null when the Makefile includes other files or uses pattern rules, since targets can't be listed.</summary>
    public static IReadOnlySet<string>? MakeTargets(string? makefile)
    {
        if (makefile is null || MakeIncludeRegex().IsMatch(makefile))
        {
            return null;
        }

        makefile = ContinuationRegex().Replace(makefile, " ");

        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in MakeRuleRegex().Matches(makefile))
        {
            foreach (var t in m.Groups["targets"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (t.Contains('%') || t.Contains("$("))
                {
                    return null;
                }

                targets.Add(t);
            }
        }

        return targets;
    }

    public static IEnumerable<string> MsBuildTargets(string? xml) =>
        xml is null ? [] : MsBuildTargetRegex().Matches(xml).Select(m => m.Groups[1].Value);

    public static bool IsMsBuildFile(string path) =>
        path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".targets", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\\\r?\n")]
    private static partial Regex ContinuationRegex();

    [GeneratedRegex(@"^[ \t]*-?include\s", RegexOptions.Multiline)]
    private static partial Regex MakeIncludeRegex();

    [GeneratedRegex(@"^(?<targets>[^\s:#=](?:[^:=#\n]*[^\s:=#])?)\s*::?(?!=)", RegexOptions.Multiline)]
    private static partial Regex MakeRuleRegex();

    [GeneratedRegex(@"<Target\s+[^>]*?Name\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex MsBuildTargetRegex();
}

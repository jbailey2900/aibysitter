using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aibysitter.Rules.PullRequests;

public enum ConclusionMode
{
    Advisory,
    FailOnErrors,
    FailOnWarnings,
}

/// <param name="Line">1-based line in the config file.</param>
public sealed record ConfigError(int Line, string Message)
{
    public override string ToString() => Message;
}

/// <summary>An <c>ignore</c> entry: paths excluded from the listed content checks, or from every content check when Checks is null.</summary>
public sealed record IgnoreEntry(IReadOnlyList<Glob> Paths, IReadOnlySet<string>? Checks);

/// <summary>A <c>dependencies.approved</c> entry: a package P010 reports as Info when a new reference's version is in <paramref name="Version"/>.</summary>
public sealed record ApprovedDependency(string Name, VersionRange Version, string Reason);

/// <summary>Parsed <c>.github/aibysitter.json</c>.</summary>
public sealed partial record RepoConfig(IReadOnlyList<Glob> Scope, ConclusionMode Conclusion)
{
    public const string FilePath = ".github/aibysitter.json";

    public static RepoConfig Default { get; } = new([], ConclusionMode.Advisory);

    /// <summary>fail-on-warnings or fail-on-errors: findings at the threshold, and config errors, fail the check.</summary>
    public bool FailsCheck => Conclusion != ConclusionMode.Advisory;

    /// <summary>The config value: advisory, fail-on-warnings or fail-on-errors.</summary>
    public static string ConclusionName(ConclusionMode mode) => mode switch
    {
        ConclusionMode.FailOnErrors => "fail-on-errors",
        ConclusionMode.FailOnWarnings => "fail-on-warnings",
        _ => "advisory",
    };

    /// <summary>Check IDs (Pnnn) and rule IDs (Rnnn) listed under <c>disable</c>. Disabled checks do not run; disabled rules are skipped by P014.</summary>
    public IReadOnlySet<string> Disabled { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Post one PR comment with the summary, updated in place (<c>"comment": true</c>).</summary>
    public bool Comment { get; init; }

    public const int MaxIgnoreEntries = 50;

    /// <summary>Checks that read paths or rules files only; <c>ignore</c> does not apply to them.</summary>
    public static IReadOnlySet<string> PathOnlyChecks { get; } = new HashSet<string>(StringComparer.Ordinal) { "P004", "P008", "P011", "P013", "P014" };

    /// <summary>Entries under <c>ignore</c>, in file order.</summary>
    public IReadOnlyList<IgnoreEntry> Ignore { get; init; } = [];

    public const int MaxApprovedDependencies = 100;

    /// <summary>Entries under <c>dependencies.approved</c>, in file order.</summary>
    public IReadOnlyList<ApprovedDependency> ApprovedDependencies { get; init; } = [];

    public bool HasScope => Scope.Count > 0;

    /// <summary>True when an <c>ignore</c> entry excludes <paramref name="path"/> from the content check <paramref name="checkId"/>.</summary>
    public bool IsIgnored(string checkId, string path) =>
        !PathOnlyChecks.Contains(checkId)
        && Ignore.Any(e => (e.Checks is null || e.Checks.Contains(checkId)) && e.Paths.Any(g => g.IsMatch(path)));

    /// <summary>True when any <c>ignore</c> entry matches <paramref name="path"/>.</summary>
    public bool IsIgnoredByAny(string path) => Ignore.Any(e => e.Paths.Any(g => g.IsMatch(path)));

    public bool IsEnabled(string checkId) => !Disabled.Contains(checkId);

    public bool InScope(string path) => Scope.Any(g => g.IsMatch(path));

    /// <summary>Parses config JSON. Invalid parts fall back to defaults and are reported in Errors.</summary>
    public static (RepoConfig Config, IReadOnlyList<ConfigError> Errors) Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return (Default, []);
        }

        var errors = new List<ConfigError>();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex)
        {
            return (Default, [new ConfigError((int)(ex.LineNumber ?? 0) + 1, $"{FilePath}: invalid JSON ({ex.Message})")]);
        }

        var lines = KeyLines(json);
        void Error(string key, string message) => errors.Add(new ConfigError(lines.GetValueOrDefault(key, 1), $"{FilePath}: {message}"));

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (Default, [new ConfigError(1, $"{FilePath}: root must be an object")]);
            }

            var scope = new List<Glob>();
            var conclusion = ConclusionMode.Advisory;
            var disabled = new HashSet<string>(StringComparer.Ordinal);
            var comment = false;
            var ignore = new List<IgnoreEntry>();
            var approved = new List<ApprovedDependency>();

            foreach (var property in doc.RootElement.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "scope" when property.Value.ValueKind == JsonValueKind.Array:
                        var index = 0;
                        foreach (var item in property.Value.EnumerateArray())
                        {
                            var key = $"scope[{index++}]";
                            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                            {
                                Error(key, "\"scope\" entries must be non-empty strings");
                            }
                            else if (Glob.TryCreate(item.GetString(), out var glob, out var globError))
                            {
                                scope.Add(glob!);
                            }
                            else
                            {
                                Error(key, $"scope entry {item.GetRawText()}: {globError}");
                            }
                        }

                        break;
                    case "scope":
                        Error("scope", "\"scope\" must be an array of path globs");
                        break;
                    case "conclusion":
                        switch (property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null)
                        {
                            case "advisory":
                                conclusion = ConclusionMode.Advisory;
                                break;
                            case "fail-on-errors":
                                conclusion = ConclusionMode.FailOnErrors;
                                break;
                            case "fail-on-warnings":
                                conclusion = ConclusionMode.FailOnWarnings;
                                break;
                            default:
                                Error("conclusion", "\"conclusion\" must be \"advisory\", \"fail-on-warnings\" or \"fail-on-errors\"");
                                break;
                        }

                        break;
                    case "disable" when property.Value.ValueKind == JsonValueKind.Array:
                        var disableIndex = 0;
                        foreach (var item in property.Value.EnumerateArray())
                        {
                            var key = $"disable[{disableIndex++}]";
                            var id = item.ValueKind == JsonValueKind.String ? item.GetString()?.Trim().ToUpperInvariant() : null;
                            if (id is not null && CheckIdRegex().IsMatch(id) && (PullRequestCheckDocs.Find(id) ?? RuleDocs.Find(id)) is not null)
                            {
                                disabled.Add(id);
                            }
                            else
                            {
                                Error(key, $"\"disable\" entry {item.GetRawText()} is not a known check or rule ID");
                            }
                        }

                        break;
                    case "disable":
                        Error("disable", "\"disable\" must be an array of check or rule IDs");
                        break;
                    case "ignore" when property.Value.ValueKind == JsonValueKind.Array:
                        var ignoreIndex = 0;
                        foreach (var item in property.Value.EnumerateArray())
                        {
                            var key = $"ignore[{ignoreIndex++}]";
                            if (ignoreIndex > MaxIgnoreEntries)
                            {
                                Error(key, $"\"ignore\" has more than {MaxIgnoreEntries} entries; the rest are skipped");
                                break;
                            }

                            if (ParseIgnoreEntry(item, out var message) is { } entry)
                            {
                                ignore.Add(entry);
                            }
                            else
                            {
                                Error(key, $"ignore entry {item.GetRawText()}: {message}");
                            }
                        }

                        break;
                    case "ignore":
                        Error("ignore", "\"ignore\" must be an array of path globs or {\"paths\", \"checks\"} objects");
                        break;
                    case "comment" when property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                        comment = property.Value.GetBoolean();
                        break;
                    case "comment":
                        Error("comment", "\"comment\" must be true or false");
                        break;
                    case "dependencies" when property.Value.ValueKind == JsonValueKind.Object:
                        foreach (var section in property.Value.EnumerateObject())
                        {
                            if (section.Name != "approved" || section.Value.ValueKind != JsonValueKind.Array)
                            {
                                Error($"dependencies.{section.Name}", section.Name == "approved"
                                    ? "\"dependencies.approved\" must be an array of {\"name\", \"version\", \"reason\"} objects"
                                    : $"unknown key \"dependencies.{section.Name}\"");
                                continue;
                            }

                            var approvedIndex = 0;
                            foreach (var item in section.Value.EnumerateArray())
                            {
                                var key = $"dependencies.approved[{approvedIndex++}]";
                                if (approvedIndex > MaxApprovedDependencies)
                                {
                                    Error(key, $"\"dependencies.approved\" has more than {MaxApprovedDependencies} entries; the rest are skipped");
                                    break;
                                }

                                if (ParseApprovedDependency(item, out var message) is { } entry)
                                {
                                    approved.Add(entry);
                                }
                                else
                                {
                                    Error(key, $"dependencies.approved entry {item.GetRawText()}: {message}");
                                }
                            }
                        }

                        break;
                    case "dependencies":
                        Error("dependencies", "\"dependencies\" must be an object with \"approved\"");
                        break;
                    default:
                        Error(property.Name, $"unknown key \"{property.Name}\"");
                        break;
                }
            }

            return (new RepoConfig(scope, conclusion) { Disabled = disabled, Comment = comment, Ignore = ignore, ApprovedDependencies = approved }, errors);
        }
    }

    private static readonly string[] ApprovedFields = ["name", "version", "reason"];

    /// <summary>An object with "name" (exact package name, no wildcards), "version" (<see cref="VersionRange"/>) and "reason", all required.</summary>
    private static ApprovedDependency? ParseApprovedDependency(JsonElement item, out string? message)
    {
        message = null;
        if (item.ValueKind != JsonValueKind.Object)
        {
            message = "must be an object with \"name\", \"version\" and \"reason\"";
            return null;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in item.EnumerateObject())
        {
            if (!ApprovedFields.Contains(field.Name))
            {
                message = $"unknown field \"{field.Name}\"";
                return null;
            }

            if (field.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(field.Value.GetString()))
            {
                message = $"\"{field.Name}\" must be a non-empty string";
                return null;
            }

            values[field.Name] = field.Value.GetString()!.Trim();
        }

        if (ApprovedFields.FirstOrDefault(f => !values.ContainsKey(f)) is { } missing)
        {
            message = $"\"{missing}\" is required";
            return null;
        }

        if (values["name"].Contains('*', StringComparison.Ordinal) || values["version"].Contains('*', StringComparison.Ordinal))
        {
            message = "wildcards are not allowed in \"name\" or \"version\"";
            return null;
        }

        if (!VersionRange.TryParse(values["version"], out var range))
        {
            message = $"\"version\" {values["version"]} is not a version or a [min,max] range";
            return null;
        }

        return new ApprovedDependency(values["name"], range!, values["reason"]);
    }

    /// <summary>A glob string, or an object with "paths" (globs, required) and "checks" (content check IDs, optional).</summary>
    private static IgnoreEntry? ParseIgnoreEntry(JsonElement item, out string? message)
    {
        message = null;
        if (item.ValueKind == JsonValueKind.String)
        {
            if (Glob.TryCreate(item.GetString(), out var glob, out var globError))
            {
                return new IgnoreEntry([glob!], null);
            }

            message = globError ?? "must be a non-empty path glob";
            return null;
        }

        if (item.ValueKind != JsonValueKind.Object)
        {
            message = "must be a path glob or an object with \"paths\"";
            return null;
        }

        var paths = new List<Glob>();
        HashSet<string>? checks = null;
        var hasPaths = false;
        foreach (var field in item.EnumerateObject())
        {
            switch (field.Name)
            {
                case "paths" when field.Value.ValueKind == JsonValueKind.Array && field.Value.GetArrayLength() > 0:
                    hasPaths = true;
                    foreach (var path in field.Value.EnumerateArray())
                    {
                        if (path.ValueKind != JsonValueKind.String || !Glob.TryCreate(path.GetString(), out var glob, out var globError))
                        {
                            message = $"\"paths\" entry {path.GetRawText()} is not a valid path glob";
                            return null;
                        }

                        paths.Add(glob!);
                    }

                    break;
                case "checks" when field.Value.ValueKind == JsonValueKind.Array && field.Value.GetArrayLength() > 0:
                    checks = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var check in field.Value.EnumerateArray())
                    {
                        var id = check.ValueKind == JsonValueKind.String ? check.GetString()?.Trim().ToUpperInvariant() : null;
                        if (id is null || !id.StartsWith('P') || PullRequestCheckDocs.Find(id) is null)
                        {
                            message = $"\"checks\" entry {check.GetRawText()} is not a known check ID";
                            return null;
                        }

                        if (PathOnlyChecks.Contains(id))
                        {
                            message = $"{id} checks paths, not content, and cannot be ignored";
                            return null;
                        }

                        checks.Add(id);
                    }

                    break;
                default:
                    message = $"\"{field.Name}\" must be {(field.Name is "paths" or "checks" ? "a non-empty array" : "\"paths\" or \"checks\"")}";
                    return null;
            }
        }

        if (!hasPaths)
        {
            message = "\"paths\" is required";
            return null;
        }

        return new IgnoreEntry(paths, checks);
    }

    /// <summary>1-based line of each top-level key (<c>scope</c>) and of each item in a top-level array (<c>scope[0]</c>).</summary>
    private static Dictionary<string, int> KeyLines(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var lineStarts = new List<long> { 0 };
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\n')
            {
                lineStarts.Add(i + 1);
            }
        }

        int LineAt(long offset)
        {
            var found = lineStarts.BinarySearch(offset);
            return (found >= 0 ? found : ~found - 1) + 1;
        }

        var lines = new Dictionary<string, int>(StringComparer.Ordinal);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        string? property = null, section = null;
        var index = 0;
        var sectionIndex = 0;
        while (reader.Read())
        {
            if (reader.CurrentDepth == 1 && reader.TokenType == JsonTokenType.PropertyName)
            {
                property = reader.GetString();
                lines[property!] = LineAt(reader.TokenStartIndex);
                index = 0;
                section = null;
            }
            else if (reader.CurrentDepth == 2 && property == "dependencies" && reader.TokenType == JsonTokenType.PropertyName)
            {
                section = reader.GetString();
                lines[$"dependencies.{section}"] = LineAt(reader.TokenStartIndex);
                sectionIndex = 0;
            }
            else if (reader.CurrentDepth == 3 && section is not null && reader.TokenType is not (JsonTokenType.EndArray or JsonTokenType.EndObject or JsonTokenType.PropertyName))
            {
                lines.TryAdd($"dependencies.{section}[{sectionIndex++}]", LineAt(reader.TokenStartIndex));
            }
            else if (reader.CurrentDepth == 2 && property is not null && reader.TokenType is not (JsonTokenType.EndArray or JsonTokenType.EndObject or JsonTokenType.PropertyName))
            {
                lines.TryAdd($"{property}[{index++}]", LineAt(reader.TokenStartIndex));
            }
        }

        return lines;
    }

    [GeneratedRegex(@"^[PR]\d{3}$")]
    private static partial Regex CheckIdRegex();
}

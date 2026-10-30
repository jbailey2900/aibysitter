using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Aibysitter.Rules.PullRequests;

namespace Aibysitter.Web.Config;

/// <summary>
/// Writes <c>.github/aibysitter.json</c>: keys in the order scope, conclusion, disable, ignore, dependencies, comment; 2-space indent;
/// LF line ends. <c>conclusion</c> is always written; <c>scope</c>, <c>disable</c>, <c>ignore</c> and <c>dependencies</c> only when
/// non-empty; <c>comment</c> only when true.
/// </summary>
public static class RepoConfigWriter
{
    private static readonly JsonWriterOptions Options = new()
    {
        Indented = true,
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string ConclusionValue(ConclusionMode mode) => RepoConfig.ConclusionName(mode);

    /// <summary>One glob per line; lines trimmed, blank lines and repeats dropped, order kept.</summary>
    public static IReadOnlyList<string> ScopeLines(string? text) =>
        (text ?? string.Empty).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// One <c>dependencies.approved</c> entry per line: <c>&lt;name&gt; &lt;version&gt; &lt;reason…&gt;</c>, the reason being the rest of the
    /// line. Blank lines dropped. A missing version or reason is left out of the entry, so the parser reports it.
    /// </summary>
    public static IReadOnlyList<(string Name, string? Version, string? Reason)> ApprovedLines(string? text) =>
        (text ?? string.Empty).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Select(l =>
        {
            var parts = l.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
            return (parts[0], parts.Length > 1 ? parts[1] : null, parts.Length > 2 ? parts[2].Trim() : null);
        }).ToList();

    public static string Write(
        IReadOnlyList<string> scope,
        ConclusionMode conclusion,
        IReadOnlyList<string> disable,
        bool comment = false,
        IReadOnlyList<string>? ignore = null,
        IReadOnlyList<(string Name, string? Version, string? Reason)>? approved = null)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, Options))
        {
            writer.WriteStartObject();
            if (scope.Count > 0)
            {
                WriteArray(writer, "scope", scope);
            }

            writer.WriteString("conclusion", ConclusionValue(conclusion));
            if (disable.Count > 0)
            {
                WriteArray(writer, "disable", disable);
            }

            if (ignore is { Count: > 0 })
            {
                WriteArray(writer, "ignore", ignore);
            }

            if (approved is { Count: > 0 })
            {
                writer.WriteStartObject("dependencies");
                writer.WriteStartArray("approved");
                foreach (var (name, version, reason) in approved)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", name);
                    if (version is not null)
                    {
                        writer.WriteString("version", version);
                    }

                    if (reason is not null)
                    {
                        writer.WriteString("reason", reason);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            if (comment)
            {
                writer.WriteBoolean("comment", true);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    private static void WriteArray(Utf8JsonWriter writer, string name, IReadOnlyList<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }
}

using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Aibysitter.Packs;
using Aibysitter.Rules;
using Aibysitter.Rules.PullRequests;
using Aibysitter.Web.Content;

namespace Aibysitter.Web.Incidents;

/// <param name="CaughtBy">Rule and check IDs that would have caught it; empty for none.</param>
/// <param name="Source">Issue URL or site path; null when not given.</param>
public sealed record Incident(
    int Id,
    string Slug,
    string Title,
    DateOnly Date,
    string Agent,
    string? Model,
    string Submitter,
    string? Source,
    IReadOnlyList<string> CaughtBy,
    string Html);

/// <summary>
/// Incidents embedded from Incidents/Content/NNNN-slug.md: frontmatter, then the H2 sections in <see cref="Sections"/> order.
/// Loading validates every file and throws <see cref="IncidentFormatException"/> with every problem found. Newest first.
/// </summary>
public sealed partial class IncidentCatalog
{
    public const string ResourcePrefix = "incidents/";
    public const string License = "CC BY 4.0";
    public const string LicenseUrl = "https://creativecommons.org/licenses/by/4.0/";
    public const string IntakeRepoUrl = "https://github.com/jbailey2900/aibysitter-incidents";
    public const string SubmitUrl = IntakeRepoUrl + "/issues/new?template=incident.yml";
    public const string None = "none";
    public const string OptionalSection = "Reproduced by";

    public static readonly IReadOnlyList<string> Sections = ["What happened", "Impact", "Detection", "Why review and CI missed it", "Fix", OptionalSection];

    private static readonly string[] RequiredKeys = ["id", "title", "date", "agent", "submitter", "caught-by"];
    private static readonly string[] AllowedKeys = [.. RequiredKeys, "model", "source"];

    public IncidentCatalog()
        : this(typeof(IncidentCatalog).Assembly)
    {
    }

    internal IncidentCatalog(Assembly assembly)
        : this(EmbeddedFiles.Read(assembly, ResourcePrefix))
    {
    }

    /// <param name="files">File name below the incidents root to content.</param>
    public IncidentCatalog(IReadOnlyDictionary<string, string> files)
    {
        var errors = new List<string>();
        var incidents = new List<Incident>();
        foreach (var (name, text) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            if (Load(name, text, errors) is { } incident)
            {
                incidents.Add(incident);
            }
        }

        foreach (var dup in incidents.GroupBy(i => i.Id).Where(g => g.Count() > 1))
        {
            errors.Add($"id {dup.Key} is used more than once");
        }

        if (errors.Count > 0)
        {
            throw new IncidentFormatException(errors);
        }

        All = incidents.OrderByDescending(i => i.Id).ToList();
    }

    public IReadOnlyList<Incident> All { get; }

    public Incident? Find(int id) => All.FirstOrDefault(i => i.Id == id);

    public static IReadOnlySet<string> CheckIds { get; } =
        RuleDocs.All.Concat(PullRequestCheckDocs.All).Select(d => d.Id).ToHashSet(StringComparer.Ordinal);

    private static Incident? Load(string name, string text, List<string> errors)
    {
        var count = errors.Count;
        if (FileNameRegex().Match(name) is not { Success: true } file)
        {
            errors.Add($"{name}: incident files are named NNNN-slug.md");
            return null;
        }

        if (FrontmatterBlock.Parse(text) is not { } parsed)
        {
            errors.Add($"{name}: frontmatter block is missing");
            return null;
        }

        var (meta, body) = parsed;

        foreach (var key in meta.Keys.Where(k => !AllowedKeys.Contains(k)))
        {
            errors.Add($"{name}: unknown key \"{key}\"");
        }

        foreach (var key in RequiredKeys.Where(k => !meta.TryGetValue(k, out var v) || v.Length == 0))
        {
            errors.Add($"{name}: \"{key}\" is required");
        }

        var id = 0;
        if (meta.TryGetValue("id", out var idText) && idText.Length > 0
            && (!int.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out id) || id != int.Parse(file.Groups["id"].Value, CultureInfo.InvariantCulture)))
        {
            errors.Add($"{name}: id must match the file number");
        }

        var date = default(DateOnly);
        if (meta.TryGetValue("date", out var dateText) && dateText.Length > 0
            && !DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            errors.Add($"{name}: date must be yyyy-MM-dd");
        }

        if (meta.TryGetValue("submitter", out var submitter) && submitter.Length > 0 && !HandleRegex().IsMatch(submitter))
        {
            errors.Add($"{name}: submitter must be a GitHub username");
        }

        var source = meta.GetValueOrDefault("source") is { Length: > 0 } s ? s : null;
        if (source is not null && !IsSitePath(source) && !source.StartsWith("https://", StringComparison.Ordinal))
        {
            errors.Add($"{name}: source must be an https URL or a site path");
        }

        var caughtBy = ParseCaughtBy(name, meta.GetValueOrDefault("caught-by") ?? "", errors);
        CheckSections(name, body, errors);

        return errors.Count == count
            ? new Incident(
                id,
                file.Groups["slug"].Value,
                meta["title"],
                date,
                meta["agent"],
                meta.GetValueOrDefault("model") is { Length: > 0 } model ? model : null,
                submitter!,
                source,
                caughtBy,
                MarkdownRenderer.ToHtml(body))
            : null;
    }

    private static IReadOnlyList<string> ParseCaughtBy(string name, string value, List<string> errors)
    {
        if (value.Length == 0 || value == None)
        {
            return [];
        }

        var ids = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var id in ids.Where(i => !CheckIds.Contains(i)))
        {
            errors.Add(id == None ? $"{name}: caught-by is \"none\" or a list of IDs, not both" : $"{name}: caught-by has unknown ID \"{id}\"");
        }

        return ids;
    }

    private static void CheckSections(string name, string body, List<string> errors)
    {
        var lines = MarkdownLines.WithFences(body.Split('\n')).ToList();
        if (lines.Any(l => !l.InFence && l.Line.StartsWith("# ", StringComparison.Ordinal)))
        {
            errors.Add($"{name}: no H1; the title comes from the frontmatter");
        }

        var required = Sections.Take(Sections.Count - 1).ToList();
        var headings = lines.Where(l => !l.InFence && l.Line.StartsWith("## ", StringComparison.Ordinal)).Select(l => l.Line[3..].Trim()).ToList();
        if (!headings.SequenceEqual(required) && !headings.SequenceEqual(Sections))
        {
            errors.Add($"{name}: sections must be {string.Join(", ", required.Select(h => $"\"{h}\""))}, then optionally \"{OptionalSection}\", in that order");
            return;
        }

        string? current = null;
        var empty = new List<string>();
        foreach (var (line, inFence) in lines)
        {
            if (!inFence && line.StartsWith("## ", StringComparison.Ordinal))
            {
                current = line[3..].Trim();
                empty.Add(current);
            }
            else if (current is not null && line.Trim().Length > 0)
            {
                empty.Remove(current);
            }
        }

        errors.AddRange(empty.Select(h => $"{name}: section \"{h}\" is empty"));
    }

    /// <summary>A root-relative path on this site; <c>//host</c> and <c>/\host</c> are off-site in browsers.</summary>
    private static bool IsSitePath(string source) => source.StartsWith('/') && !source.StartsWith("//", StringComparison.Ordinal) && !source.StartsWith("/\\", StringComparison.Ordinal);

    [GeneratedRegex(@"^(?<id>\d{4})-(?<slug>[a-z0-9]+(?:-[a-z0-9]+)*)\.md$")]
    private static partial Regex FileNameRegex();

    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9]|-(?=[A-Za-z0-9])){0,38}$")]
    private static partial Regex HandleRegex();
}

public sealed class IncidentFormatException(IReadOnlyList<string> errors)
    : Exception("Invalid incidents:\n" + string.Join("\n", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

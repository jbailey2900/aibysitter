using System.Text.RegularExpressions;

namespace Aibysitter.Rules.PullRequests;

/// <summary>
/// Packages added by the pull request: .csproj / .props PackageReference and PackageVersion, package.json dependency
/// sections, requirements*.txt, go.mod require. A name both removed and added in the same file (version change) is not new.
/// One finding per package per ecosystem. With central package management, a PackageVersion is a Warning only when the pull
/// request also adds a PackageReference for it (reported on the first such line, by path); a PackageVersion alone is Info,
/// "Version pinned (transitive)". A new nuget PackageReference whose version (VersionOverride, Version, or a PackageVersion added
/// in the pull request) is in a <c>dependencies.approved</c> entry for its name is Info, "Approved dependency".
/// </summary>
public sealed partial class NewDependencies : IPullRequestCheck, INotesSource
{
    private static readonly HashSet<string> DependencySections = new(StringComparer.Ordinal)
    {
        "dependencies", "devDependencies", "peerDependencies", "optionalDependencies",
    };

    private static readonly HashSet<string> NonPackageKeys = new(StringComparer.Ordinal)
    {
        "name", "version", "node", "npm", "pnpm", "yarn", "bun",
    };

    public string Id => "P010";
    public string Title => "New dependencies";
    public Severity Severity => Severity.Warning;

    public IEnumerable<PullRequestFinding> Evaluate(PullRequestContext context) => Review(context).Select(r => r.Finding);

    public IEnumerable<string> Notes(PullRequestContext context) => Review(context).Where(r => r.Note is not null).Select(r => r.Note!);

    private IEnumerable<(PullRequestFinding Finding, string? Note)> Review(PullRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return Added(context)
            .GroupBy(a => (a.Ecosystem, Package: a.Package.ToUpperInvariant()))
            .Select(g =>
            {
                var versions = g.Where(IsPackageVersion).ToList();
                var references = g.Where(a => !IsPackageVersion(a)).OrderBy(a => a.Path, StringComparer.Ordinal).ThenBy(a => a.Line.NewLine).ToList();
                var name = versions.Count > 0 ? versions[0].Package : references[0].Package;
                if (references.Count == 0)
                {
                    return (new PullRequestFinding(Id, versions[0].Path, versions[0].Line.NewLine!.Value, $"Version pinned (transitive): {name}", PinFixHint, Severity.Info), (string?)null);
                }

                var reference = references[0];
                var finding = new PullRequestFinding(Id, reference.Path, reference.Line.NewLine!.Value, $"New dependency: {name}", FixHint);
                var entries = g.Key.Ecosystem == "nuget"
                    ? context.Config.ApprovedDependencies.Where(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)).ToList()
                    : [];
                if (entries.Count == 0)
                {
                    return (finding, null);
                }

                var versionText = VersionOf(reference.Line.Text, versions);
                if (versionText is null || !PackageVersion.TryParse(versionText, out var version))
                {
                    return (finding, $"Approved dependency entry for {name} not applied: the pull request does not show the version.");
                }

                if (entries.FirstOrDefault(a => a.Version.Contains(version!)) is { } approved)
                {
                    return (finding with { Message = $"Approved dependency: {name} ({approved.Reason})", Severity = Severity.Info }, null);
                }

                return (finding, $"Approved dependency entry for {name} not applied: version {versionText} is outside {string.Join(", ", entries.Select(a => a.Version.Text))}.");
            });
    }

    /// <summary>VersionOverride or Version on the reference line, else Version on a PackageVersion added in the pull request.</summary>
    private static string? VersionOf(string referenceLine, List<(string Ecosystem, string Path, DiffLine Line, string Package)> versions)
    {
        var own = VersionOverrideRegex().Match(referenceLine) is { Success: true } o ? o : VersionAttributeRegex().Match(referenceLine);
        if (own.Success)
        {
            return own.Groups["v"].Value;
        }

        return versions.Select(v => VersionAttributeRegex().Match(v.Line.Text)).FirstOrDefault(m => m.Success)?.Groups["v"].Value;
    }

    private const string FixHint = "Confirm the package was requested, or remove it and use what the project already has.";

    private const string PinFixHint = "No project references the package in this pull request; the entry sets the version of a transitive dependency.";

    private static bool IsPackageVersion((string Ecosystem, string Path, DiffLine Line, string Package) entry) =>
        entry.Ecosystem == "nuget" && PackageVersionRegex().IsMatch(entry.Line.Text);

    private static IEnumerable<(string Ecosystem, string Path, DiffLine Line, string Package)> Added(PullRequestContext context)
    {
        foreach (var file in context.Files.Where(f => f.Status != FileChangeStatus.Removed && !CodeText.IsGenerated(f)))
        {
            var name = Path.GetFileName(file.Path);
            var (ecosystem, extract) = Extractor(name);
            if (extract is null)
            {
                continue;
            }

            var entries = extract(file.Lines).ToList();
            var removed = entries.Where(e => e.Line.Kind == DiffLineKind.Removed).Select(e => e.Package).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var (line, package) in entries.Where(e => e.Line.Kind == DiffLineKind.Added && !removed.Contains(e.Package)))
            {
                yield return (ecosystem, file.Path, line, package);
            }
        }
    }

    private static (string Ecosystem, Func<IReadOnlyList<DiffLine>, IEnumerable<(DiffLine Line, string Package)>>? Extract) Extractor(string fileName) => fileName switch
    {
        _ when fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".targets", StringComparison.OrdinalIgnoreCase) => ("nuget", lines => ByRegex(lines, PackageReferenceRegex())),
        "package.json" => ("npm", PackageJson),
        _ when RequirementsFileRegex().IsMatch(fileName) => ("pip", lines => ByRegex(lines, RequirementRegex())),
        "go.mod" => ("go", lines => ByRegex(lines, GoRequireRegex())),
        _ => (string.Empty, null),
    };

    private static IEnumerable<(DiffLine, string)> ByRegex(IReadOnlyList<DiffLine> lines, Regex regex) =>
        lines.Where(l => l.Kind != DiffLineKind.Context)
            .Select(l => (Line: l, Match: regex.Match(l.Text)))
            .Where(x => x.Match.Success)
            .Select(x => (x.Line, x.Match.Groups["name"].Value));

    /// <summary>
    /// Tracks the enclosing section from "key": { lines in the diff; resets at each hunk (line-number gap).
    /// Unknown section (hunk starts mid-object):
    /// accepts entries whose value looks like a version or package spec.
    /// </summary>
    private static IEnumerable<(DiffLine, string)> PackageJson(IReadOnlyList<DiffLine> lines)
    {
        string? section = null;
        int? lastOld = null, lastNew = null;
        foreach (var line in lines)
        {
            var newHunk = (line.OldLine is { } o && lastOld is { } lo && o > lo + 1) || (line.NewLine is { } n && lastNew is { } ln && n > ln + 1);
            lastOld = line.OldLine ?? lastOld;
            lastNew = line.NewLine ?? lastNew;
            if (newHunk)
            {
                section = null;
            }

            var open = ObjectOpenRegex().Match(line.Text);
            if (open.Success)
            {
                section = open.Groups["key"].Value;
                continue;
            }

            if (ObjectCloseRegex().IsMatch(line.Text))
            {
                section = string.Empty;
                continue;
            }

            if (line.Kind == DiffLineKind.Context)
            {
                continue;
            }

            var entry = JsonEntryRegex().Match(line.Text);
            if (!entry.Success)
            {
                continue;
            }

            var key = entry.Groups["name"].Value;
            var isDependency = section switch
            {
                null => !NonPackageKeys.Contains(key) && VersionSpecRegex().IsMatch(entry.Groups["value"].Value),
                _ => DependencySections.Contains(section),
            };

            if (isDependency)
            {
                yield return (line, key);
            }
        }
    }

    [GeneratedRegex(@"<Package(?:Reference|Version)\s+(?:[^>]*\s)?Include\s*=\s*""(?<name>[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex PackageReferenceRegex();

    [GeneratedRegex(@"\bVersionOverride\s*=\s*""(?<v>[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex VersionOverrideRegex();

    [GeneratedRegex(@"\bVersion\s*=\s*""(?<v>[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex VersionAttributeRegex();

    [GeneratedRegex(@"<PackageVersion\s", RegexOptions.IgnoreCase)]
    private static partial Regex PackageVersionRegex();

    [GeneratedRegex(@"^requirements[\w.-]*\.txt$", RegexOptions.IgnoreCase)]
    private static partial Regex RequirementsFileRegex();

    [GeneratedRegex(@"^\s*(?<name>[A-Za-z0-9][A-Za-z0-9._-]*)\s*(?:\[[^\]]*\])?\s*(?:[=<>!~;@]|$)")]
    private static partial Regex RequirementRegex();

    [GeneratedRegex(@"^\s*(?:require\s+)?(?<name>[\w-]+(?:\.[\w-]+)+/[\w./-]+)\s+v\d")]
    private static partial Regex GoRequireRegex();

    [GeneratedRegex(@"^\s*""(?<key>[^""]+)""\s*:\s*\{\s*$")]
    private static partial Regex ObjectOpenRegex();

    [GeneratedRegex(@"^\s*\},?\s*$")]
    private static partial Regex ObjectCloseRegex();

    [GeneratedRegex(@"^\s*""(?<name>@?[^""\s]+)""\s*:\s*""(?<value>[^""]*)""")]
    private static partial Regex JsonEntryRegex();

    [GeneratedRegex(@"^(?:[\^~<>=*]|\d|workspace:|npm:|file:|link:|git|https?:|latest$)")]
    private static partial Regex VersionSpecRegex();
}

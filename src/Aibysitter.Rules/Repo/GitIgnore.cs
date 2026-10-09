using System.Text;
using System.Text.RegularExpressions;

namespace Aibysitter.Rules.Repo;

/// <summary>
/// Subset of .gitignore matching: comments, blank lines, anchored (leading or inner slash) and unanchored patterns,
/// trailing-slash directories, *, **, ?. Negation lines (!) are ignored, so a re-included path still counts as ignored.
/// Lines with more than <see cref="PullRequests.Glob.MaxWildcards"/> wildcards are skipped.
/// </summary>
public sealed class GitIgnore
{
    private readonly List<(Regex Pattern, string BaseDir)> patterns = [];

    public void Add(string? content, string baseDir)
    {
        if (content is null)
        {
            return;
        }

        foreach (var raw in content.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('!'))
            {
                continue;
            }

            var dirOnly = line.EndsWith('/');
            line = line.TrimEnd('/');
            var anchored = line.Contains('/');
            line = line.TrimStart('/');
            if (PullRequests.Glob.WildcardCount(line) > PullRequests.Glob.MaxWildcards)
            {
                continue;
            }

            var body = ToRegex(line);
            var regex = anchored ? $"^{body}(?:/.*)?$" : $"(?:^|/){body}(?:/.*)?$";
            if (dirOnly)
            {
                regex = anchored ? $"^{body}/.*$|^{body}$" : $"(?:^|/){body}(?:/.*)?$";
            }

            patterns.Add((new Regex(regex, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking), RepoSnapshot.Normalize(baseDir)));
        }
    }

    public bool IsIgnored(string path)
    {
        var p = RepoSnapshot.Normalize(path);
        foreach (var (pattern, baseDir) in patterns)
        {
            if (baseDir.Length == 0)
            {
                if (pattern.IsMatch(p))
                {
                    return true;
                }
            }
            else if (p.StartsWith(baseDir + "/", StringComparison.Ordinal) && pattern.IsMatch(p[(baseDir.Length + 1)..]))
            {
                return true;
            }
        }

        return false;
    }

    private static string ToRegex(string glob)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                sb.Append(".*");
                i++;
                if (i + 1 < glob.Length && glob[i + 1] == '/')
                {
                    i++;
                    sb.Append("/?");
                }
            }
            else if (c == '*')
            {
                sb.Append("[^/]*");
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }

        return sb.ToString();
    }
}

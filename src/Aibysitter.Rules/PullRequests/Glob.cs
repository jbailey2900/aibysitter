using System.Text;
using System.Text.RegularExpressions;

namespace Aibysitter.Rules.PullRequests;

/// <summary>
/// Path glob anchored at the repo root, '/' separated, case-sensitive.
/// '**' matches any number of segments (including none), '*' any run within a segment, '?' one character within a segment.
/// </summary>
public sealed partial class Glob
{
    /// <summary>Most wildcards (each *, ** and ?) one pattern may hold.</summary>
    public const int MaxWildcards = 8;

    private readonly Regex regex;

    /// <summary>Throws <see cref="ArgumentException"/> with the <see cref="Validate"/> message for an unsupported pattern.</summary>
    public Glob(string pattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        if (Validate(pattern) is { } error)
        {
            throw new ArgumentException(error, nameof(pattern));
        }

        Pattern = pattern.Trim().TrimStart('/');
        regex = new Regex(ToRegex(Pattern), RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    }

    public string Pattern { get; }

    public static bool TryCreate(string? pattern, out Glob? glob, out string? error)
    {
        glob = null;
        error = string.IsNullOrWhiteSpace(pattern) ? "pattern is empty" : Validate(pattern);
        if (error is null)
        {
            glob = new Glob(pattern!);
        }

        return glob is not null;
    }

    /// <summary>Why the pattern is not supported; null when it is. A leading '/' is allowed and ignored.</summary>
    public static string? Validate(string pattern)
    {
        var p = pattern.Trim().TrimStart('/');
        if (p.Length == 0)
        {
            return "pattern is empty";
        }

        if (p.Contains('\\'))
        {
            return "use / as the path separator";
        }

        if (p.StartsWith('!'))
        {
            return "! negation is not supported; list the paths to include";
        }

        if (p.IndexOfAny(['{', '}']) >= 0)
        {
            return "{a,b} alternatives are not supported; list each pattern";
        }

        if (p.IndexOfAny(['[', ']']) >= 0)
        {
            return "[...] character classes are not supported";
        }

        if (p.EndsWith('/'))
        {
            return $"a trailing / matches nothing; use {p}**";
        }

        var segments = p.Split('/');
        if (segments.Any(s => s.Length == 0))
        {
            return "empty path segment (//)";
        }

        if (segments.Any(s => s is "." or ".."))
        {
            return "paths start at the repository root; remove ./ and ../";
        }

        if (segments.Any(s => s.Contains("**", StringComparison.Ordinal) && s != "**"))
        {
            return "** must be a whole path segment; for files in any folder use **/*.cs";
        }

        if (WildcardCount(p) > MaxWildcards)
        {
            return $"at most {MaxWildcards} wildcards (*, **, ?) per pattern";
        }

        return null;
    }

    /// <summary>Each *, ** and ? counts once.</summary>
    public static int WildcardCount(string pattern) => WildcardRegex().Count(pattern);

    [GeneratedRegex(@"\*\*|\*|\?")]
    private static partial Regex WildcardRegex();

    public bool IsMatch(string path) => regex.IsMatch(path.TrimStart('/'));

    private static string ToRegex(string pattern)
    {
        var sb = new StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                var atSegmentStart = i == 0 || pattern[i - 1] == '/';
                var followedBySlash = i + 2 < pattern.Length && pattern[i + 2] == '/';
                var atEnd = i + 2 == pattern.Length;

                if (atSegmentStart && followedBySlash)
                {
                    sb.Append("(?:.*/)?");
                    i += 2;
                }
                else if (atSegmentStart && atEnd && i > 0)
                {
                    sb.Length -= 1;
                    sb.Append("(?:/.*)?");
                    i += 1;
                }
                else
                {
                    sb.Append(".*");
                    i += 1;
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

        return sb.Append('$').ToString();
    }
}

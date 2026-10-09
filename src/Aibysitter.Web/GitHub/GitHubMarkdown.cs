using System.Text;

namespace Aibysitter.Web.GitHub;

/// <summary>
/// Puts text from a pull request (paths, messages quoting its lines, config values) into GitHub Markdown inertly:
/// no formatting, links, HTML, mentions or line breaks. Used by the check summary and the PR comment.
/// </summary>
public static class GitHubMarkdown
{
    private const string Escaped = "\\`*_[]()<>!#|~";
    private const char ZeroWidthJoiner = '‍';

    /// <summary>One line; Markdown punctuation backslash-escaped; a zero-width joiner after each @.</summary>
    public static string Text(string? value)
    {
        var sb = new StringBuilder();
        foreach (var c in OneLine(value))
        {
            if (Escaped.Contains(c))
            {
                sb.Append('\\');
            }

            sb.Append(c);
            if (c == '@')
            {
                sb.Append(ZeroWidthJoiner);
            }
        }

        return sb.ToString();
    }

    /// <summary>One line as a code span, fenced with one more backtick than the longest run inside.</summary>
    public static string Code(string? value)
    {
        var text = OneLine(value);
        var longest = 0;
        var run = 0;
        foreach (var c in text)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        var fence = new string('`', longest + 1);
        var pad = text.StartsWith('`') || text.EndsWith('`') || (text.Length > 0 && text.Trim().Length == 0) ? " " : "";
        return fence + pad + text + pad + fence;
    }

    private static string OneLine(string? value) =>
        string.Join(' ', (value ?? "").Split((char[])['\r', '\n', '\u0085', '\u2028', '\u2029'], StringSplitOptions.RemoveEmptyEntries)).Trim();
}

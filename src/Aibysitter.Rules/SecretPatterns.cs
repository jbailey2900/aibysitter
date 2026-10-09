using System.Text.RegularExpressions;

namespace Aibysitter.Rules;

/// <summary>Credential patterns shared by R009 and P005. Matches containing a placeholder marker, or equal to a common example password, are ignored.</summary>
public static partial class SecretPatterns
{
    public sealed record SecretMatch(string Kind, int Column, string Redacted);

    private static readonly (string Kind, Regex Regex)[] Patterns =
    [
        ("Private key", PrivateKeyRegex()),
        ("AWS access key ID", AwsKeyRegex()),
        ("GitHub token", GitHubTokenRegex()),
        ("Slack token", SlackTokenRegex()),
        ("Anthropic API key", AnthropicKeyRegex()),
        ("OpenAI API key", OpenAiKeyRegex()),
        ("Stripe live key", StripeKeyRegex()),
        ("Google API key", GoogleKeyRegex()),
        ("JSON Web Token", JwtRegex()),
        ("Password in connection string", ConnectionPasswordRegex()),
        ("Credentials in URL", UrlCredentialsRegex()),
    ];

    /// <summary>All secret-like values in one line, left to right, without the secret text.</summary>
    public static IReadOnlyList<SecretMatch> Find(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var found = new List<SecretMatch>();
        foreach (var (kind, regex) in Patterns)
        {
            foreach (Match m in regex.Matches(line))
            {
                var value = m.Groups["v"].Success ? m.Groups["v"] : (Group)m;
                if (IsPlaceholder(value.Value) || found.Any(f => f.Column == value.Index + 1))
                {
                    continue;
                }

                found.Add(new SecretMatch(kind, value.Index + 1, Redact(value.Value)));
            }
        }

        return found.OrderBy(f => f.Column).ToList();
    }

    private static string Redact(string value) => value.Length <= 8 ? "****" : $"{value[..4]}…";

    private static bool IsPlaceholder(string value) => PlaceholderRegex().IsMatch(value) || CommonValueRegex().IsMatch(value);

    [GeneratedRegex(@"x{4,}|\.\.\.|[<>{}$*]|your|example|sample|placeholder|changeme|redacted|dummy|fake|test|secret_here|password\b", RegexOptions.IgnoreCase)]
    private static partial Regex PlaceholderRegex();

    /// <summary>Whole values used as example passwords.</summary>
    [GeneratedRegex(@"^(?:pass|passwd|pwd|abc123|123456|12345678|qwerty)$", RegexOptions.IgnoreCase)]
    private static partial Regex CommonValueRegex();

    [GeneratedRegex(@"-----BEGIN (?:RSA |EC |DSA |OPENSSH |PGP |ENCRYPTED )?PRIVATE KEY-----")]
    private static partial Regex PrivateKeyRegex();

    [GeneratedRegex(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b")]
    private static partial Regex AwsKeyRegex();

    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{22,})\b")]
    private static partial Regex GitHubTokenRegex();

    [GeneratedRegex(@"\bxox[abprs]-[A-Za-z0-9-]{10,}")]
    private static partial Regex SlackTokenRegex();

    [GeneratedRegex(@"\bsk-ant-[A-Za-z0-9_-]{20,}")]
    private static partial Regex AnthropicKeyRegex();

    [GeneratedRegex(@"\bsk-(?!ant-)(?:proj-)?[A-Za-z0-9_-]{20,}")]
    private static partial Regex OpenAiKeyRegex();

    [GeneratedRegex(@"\b[rs]k_live_[0-9A-Za-z]{20,}")]
    private static partial Regex StripeKeyRegex();

    [GeneratedRegex(@"\bAIza[0-9A-Za-z_-]{35}\b")]
    private static partial Regex GoogleKeyRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9_-])eyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}")]
    private static partial Regex JwtRegex();

    [GeneratedRegex(@"\b(?:Password|Pwd)\s*=\s*(?<v>[^;\s""'`]{4,})", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionPasswordRegex();

    [GeneratedRegex(@"(?<![a-z0-9+.-])[a-z][a-z0-9+.-]*://[^\s:/@]+:(?<v>[^\s:/@]{4,})@(?!(?:localhost|127\.\d+\.\d+\.\d+|0\.0\.0\.0|\[::1\])\b)", RegexOptions.IgnoreCase)]
    private static partial Regex UrlCredentialsRegex();
}

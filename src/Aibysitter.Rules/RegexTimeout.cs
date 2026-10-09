namespace Aibysitter.Rules;

/// <summary>Process-wide match timeout for every Regex, generated ones included. Set before the first regex runs.</summary>
public static class RegexTimeout
{
    public const string AppContextKey = "REGEX_DEFAULT_MATCH_TIMEOUT";

    public static readonly TimeSpan Default = TimeSpan.FromSeconds(1);

    public const string LintMessage = "Linting stopped after 1 second. Shorten very long lines or split the file.";

    /// <summary>The setting must be a <see cref="TimeSpan"/>; a runtimeconfig string makes every Regex throw.</summary>
    public static void Apply() => AppDomain.CurrentDomain.SetData(AppContextKey, Default);
}

using System.Text.RegularExpressions;

namespace Aibysitter.Rules.PullRequests;

public static partial class FileKinds
{
    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csx", ".cshtml", ".razor", ".vb", ".fs",
        ".js", ".jsx", ".mjs", ".cjs", ".ts", ".tsx",
        ".py", ".go", ".java", ".kt", ".rb", ".rs", ".php", ".swift",
        ".c", ".h", ".cpp", ".hpp", ".sql", ".ps1", ".psm1", ".sh",
    };

    private static readonly HashSet<string> ConfigExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".yml", ".yaml", ".xml", ".config", ".csproj", ".props", ".targets", ".env", ".toml", ".ini",
    };

    public static bool IsCode(string path) => CodeExtensions.Contains(Path.GetExtension(path));

    public static bool IsCodeOrConfig(string path) => IsCode(path) || ConfigExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// Code files under a test / tests / __tests__ / spec folder, or named *Test(s).*, *.test.*, *.spec.*,
    /// test_*.py, *_test.py, *_test.go, *_spec.rb. "Test" / "Tests" in a file name is matched case-sensitively.
    /// </summary>
    public static bool IsTestFile(string path) => IsCode(path) && TestPathRegex().IsMatch(path.Replace('\\', '/'));

    /// <summary>
    /// A tests / test / __tests__ / spec(s) folder, a .NET test project folder (Foo.Tests/, Foo.UnitTests/, case-sensitive), a
    /// *Test(s).ext file name (case-sensitive), *.test.* / *.spec.*, Python test_*.py / *_test.py, Go *_test.go, Ruby *_spec.rb.
    /// </summary>
    [GeneratedRegex(@"(?:^|/)(?:tests?|__tests__|specs?)/|(?-i:(?:^|/)[^/]+\.(?:[A-Z][A-Za-z]*)?Tests?/)|(?-i:(?:^|/)[^/]*Tests?\.[^/.]+$)|\.(?:test|spec)\.[^/.]+$|(?:^|/)test_[^/]*\.py$|_test\.(?:py|go)$|_spec\.rb$", RegexOptions.IgnoreCase)]
    private static partial Regex TestPathRegex();
}

using System.Text.RegularExpressions;

namespace Aibysitter.Rules.PullRequests;

/// <summary>
/// Test files removed by the pull request. Removed test methods inside kept files are not checked.
/// Not flagged: the same pull request removes a non-test code file the test is named after
/// (FooTests.cs and Foo.cs, foo.test.ts and foo.ts, test_foo.py and foo.py, foo_test.go and foo.go, foo_spec.rb and foo.rb),
/// or removes the whole package the test belongs to. Whole package, read from the head file list: the folder above the
/// test folder holds no code files (pkg/ for pkg/test/a.spec.ts), or a test project folder X.Tests (X.UnitTests,
/// X.ConformanceTests, …) and a folder X holding removed non-test code holds no code files.
/// A justification line in the PR description (<see cref="DeletedTestJustifications"/>) downgrades the finding to Info.
/// </summary>
public sealed partial class DeletedTests : IPullRequestCheck, INotesSource
{
    public const string FixHint = "Restore the file, or add \"Aibysitter: P008 <removed test path> -> <replacement path>\" to the PR description.";

    public string Id => "P008";
    public string Title => "Deleted tests";
    public Severity Severity => Severity.Error;

    public IEnumerable<PullRequestFinding> Evaluate(PullRequestContext context)
    {
        var flagged = Flagged(context);
        var (accepted, _) = DeletedTestJustifications.Evaluate(context, flagged);
        foreach (var path in flagged)
        {
            yield return accepted.TryGetValue(path, out var j)
                ? new PullRequestFinding(
                    Id,
                    path,
                    1,
                    $"Test file deleted: {path}. Replaced by {j.Replacement} ({(j.Workflow is null ? "test file" : $"referenced by {j.Workflow}")}).",
                    FixHint,
                    Severity.Info)
                : new PullRequestFinding(Id, path, 1, $"Test file deleted: {path}", FixHint);
        }
    }

    public IEnumerable<string> Notes(PullRequestContext context) =>
        context.Description is null ? [] : DeletedTestJustifications.Evaluate(context, Flagged(context)).Notes;

    /// <summary>Removed test files flagged before justifications.</summary>
    private static List<string> Flagged(PullRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var result = new List<string>();
        var removed = context.Files.Where(f => f.Status == FileChangeStatus.Removed).ToList();
        var removedSubjects = removed
            .Where(f => FileKinds.IsCode(f.Path) && !FileKinds.IsTestFile(f.Path))
            .Select(f => Path.GetFileNameWithoutExtension(f.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var removedCode = removed.Where(f => FileKinds.IsCode(f.Path) && !FileKinds.IsTestFile(f.Path)).Select(f => f.Path).ToList();
        var headFolders = context.HeadFiles is null ? null : Folders(context.HeadFiles.Where(FileKinds.IsCode));

        foreach (var file in removed.Where(f => FileKinds.IsTestFile(f.Path)))
        {
            if (removedSubjects.Contains(Subject(file.Path))
                || (headFolders is not null && PackageRemoved(file.Path, removedCode, headFolders)))
            {
                continue;
            }

            result.Add(file.Path);
        }

        return result;
    }

    /// <summary>True when the folder holding the test's package holds no code files at the head.</summary>
    internal static bool PackageRemoved(string testPath, IReadOnlyList<string> removedCode, IReadOnlySet<string> headFolders)
    {
        var testFolder = TestFolderRegex().Match(testPath);
        if (testFolder.Success && testFolder.Index > 0 && !headFolders.Contains(testPath[..testFolder.Index]))
        {
            return true;
        }

        var segments = testPath.Split('/');
        foreach (var segment in segments[..^1])
        {
            var project = TestProjectRegex().Match(segment);
            if (!project.Success)
            {
                continue;
            }

            var subject = project.Groups["x"].Value;
            foreach (var code in removedCode)
            {
                var parts = code.Split('/');
                var at = Array.FindIndex(parts[..^1], p => p.Equals(subject, StringComparison.OrdinalIgnoreCase));
                if (at >= 0 && !headFolders.Contains(string.Join('/', parts[..(at + 1)])))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static HashSet<string> Folders(IEnumerable<string> files)
    {
        var folders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in files)
        {
            for (var i = f.IndexOf('/'); i >= 0; i = f.IndexOf('/', i + 1))
            {
                folders.Add(f[..i]);
            }
        }

        return folders;
    }

    [GeneratedRegex(@"/(?:tests?|__tests__|specs?)/", RegexOptions.IgnoreCase)]
    private static partial Regex TestFolderRegex();

    [GeneratedRegex(@"^(?<x>.+?)\.?(?:Unit|Integration|Conformance|Functional|EndToEnd)?Tests?$")]
    private static partial Regex TestProjectRegex();

    /// <summary>The file name with test affixes removed: FooTests.cs → Foo; foo.test.ts → foo; test_foo.py → foo.</summary>
    internal static string Subject(string path) => SubjectRegex().Replace(Path.GetFileName(path), string.Empty);

    [GeneratedRegex(@"^test_|(?:(?-i:Tests?)|[._](?:test|spec))?\.[^.]+$", RegexOptions.IgnoreCase)]
    private static partial Regex SubjectRegex();
}

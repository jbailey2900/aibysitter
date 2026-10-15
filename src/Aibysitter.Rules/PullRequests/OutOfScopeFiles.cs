namespace Aibysitter.Rules.PullRequests;

/// <summary>Flags changed files outside the repo config's scope globs. Runs only when the config declares scope.</summary>
public sealed class OutOfScopeFiles : IPullRequestCheck
{
    public string Id => "P004";
    public string Title => "Out-of-scope files";
    public Severity Severity => Severity.Error;

    public IEnumerable<PullRequestFinding> Evaluate(PullRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.Config.HasScope)
        {
            yield break;
        }

        foreach (var file in context.Files)
        {
            var outside = new[] { file.Path, file.PreviousPath }
                .Where(p => !string.IsNullOrEmpty(p) && !context.Config.InScope(p!))
                .Distinct()
                .ToList();

            if (outside.Count > 0)
            {
                yield return new PullRequestFinding(
                    Id,
                    file.Path,
                    1,
                    $"Outside declared scope ({file.Status.ToString().ToLowerInvariant()}): {string.Join(", ", outside)}. Widen scope or move the file to its own PR.",
                    $"Add the path to \"scope\" in {RepoConfig.FilePath}, or move the change to a pull request of its own.");
            }
        }
    }
}

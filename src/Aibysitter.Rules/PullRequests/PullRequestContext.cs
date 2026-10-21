using Aibysitter.Rules.Repo;

namespace Aibysitter.Rules.PullRequests;

/// <param name="Repo">Files at the PR head, for R006; null when not fetched.</param>
/// <param name="UnchangedRulesFiles">Rules files the PR does not change, with head content, checked by R006 against removals only.</param>
/// <param name="HeadFiles">Every file path at the PR head, for P008; null when not fetched.</param>
/// <param name="Description">The pull request description, for P008 justifications; null when not fetched.</param>
/// <param name="WorkflowFiles">Workflow files at the PR head (path to text), for P008 justifications; null when not fetched.</param>
public sealed record PullRequestContext(
    IReadOnlyList<ChangedFile> Files,
    RepoConfig Config,
    RepoSnapshot? Repo = null,
    IReadOnlyList<ChangedFile>? UnchangedRulesFiles = null,
    IReadOnlyCollection<string>? HeadFiles = null,
    string? Description = null,
    IReadOnlyDictionary<string, string>? WorkflowFiles = null);

using Aibysitter.Rules.PullRequests;

namespace Aibysitter.Web.GitHub;

public interface IGitHubGateway
{
    Task<long> CreateQueuedCheckRunAsync(PullRequestRef pr, CancellationToken cancellationToken);

    Task MarkInProgressAsync(PullRequestRef pr, long checkRunId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ChangedFile>> GetChangedFilesAsync(PullRequestRef pr, CancellationToken cancellationToken);

    /// <summary>File text at the PR head commit, or null when the file does not exist.</summary>
    Task<FileContent?> GetFileContentAsync(PullRequestRef pr, string path, CancellationToken cancellationToken);

    /// <summary>File text at the PR base commit (<see cref="PullRequestRef.BaseSha"/>), or null when the file does not exist.</summary>
    Task<FileContent?> GetBaseFileContentAsync(PullRequestRef pr, string path, CancellationToken cancellationToken);

    /// <summary>Every file path at the PR head commit and which are symlinks, or null when GitHub truncates the listing.</summary>
    Task<RepoTree?> GetTreeAsync(PullRequestRef pr, CancellationToken cancellationToken);

    Task CompleteCheckRunAsync(PullRequestRef pr, long checkRunId, CheckRunReport report, CancellationToken cancellationToken);

    /// <summary>The App's slug; its comments are authored by <c>{slug}[bot]</c>.</summary>
    Task<string> GetAppSlugAsync(CancellationToken cancellationToken);

    /// <summary>Every comment on the PR's conversation. Throws <see cref="GitHubForbiddenException"/> on 403 (also for the calls below).</summary>
    Task<IReadOnlyList<IssueCommentInfo>> ListIssueCommentsAsync(PullRequestRef pr, CancellationToken cancellationToken);

    Task CreateIssueCommentAsync(PullRequestRef pr, string body, CancellationToken cancellationToken);

    Task UpdateIssueCommentAsync(PullRequestRef pr, long commentId, string body, CancellationToken cancellationToken);

    Task DeleteIssueCommentAsync(PullRequestRef pr, long commentId, CancellationToken cancellationToken);
}

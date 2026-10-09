using System.Collections.Concurrent;
using Aibysitter.Rules.PullRequests;
using Aibysitter.Web.GitHub;

namespace Aibysitter.Rules.Tests.GitHub;

internal sealed class FakeGitHubGateway : IGitHubGateway
{
    public ConcurrentQueue<string> Calls { get; } = new();

    public List<ChangedFile> Files { get; } = [];

    public Dictionary<string, string> Contents { get; } = new(StringComparer.Ordinal);

    /// <summary>Files at the base commit.</summary>
    public Dictionary<string, string> BaseContents { get; } = new(StringComparer.Ordinal);

    /// <summary>File list at head; null simulates a truncated listing.</summary>
    public List<string>? Paths { get; set; } = [];

    /// <summary>Paths in <see cref="Paths"/> that are symlinks.</summary>
    public HashSet<string> Symlinks { get; } = new(StringComparer.Ordinal);

    public long NextCheckRunId { get; set; } = 777;

    public Exception? ThrowOnCreate { get; set; }

    public Exception? ThrowOnFiles { get; set; }

    public Exception? ThrowOnComplete { get; set; }

    public Exception? ThrowOnInProgress { get; set; }

    public TaskCompletionSource<CheckRunReport> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Slug { get; set; } = "aibysitter";

    /// <summary>PR conversation comments; ids increase in creation order.</summary>
    public List<IssueCommentInfo> Comments { get; } = [];

    public bool ForbidComments { get; set; }

    /// <summary>Added by "another process" right after this one creates its comment.</summary>
    public string? RacingCommentBody { get; set; }

    private long nextCommentId = 1000;

    public Task<long> CreateQueuedCheckRunAsync(PullRequestRef pr, CancellationToken cancellationToken)
    {
        Calls.Enqueue($"create {pr}");
        return ThrowOnCreate is null ? Task.FromResult(NextCheckRunId) : Task.FromException<long>(ThrowOnCreate);
    }

    public Task MarkInProgressAsync(PullRequestRef pr, long checkRunId, CancellationToken cancellationToken)
    {
        Calls.Enqueue($"in_progress {checkRunId}");
        return ThrowOnInProgress is null ? Task.CompletedTask : Task.FromException(ThrowOnInProgress);
    }

    public Task<IReadOnlyList<ChangedFile>> GetChangedFilesAsync(PullRequestRef pr, CancellationToken cancellationToken)
    {
        Calls.Enqueue("files");
        return ThrowOnFiles is null ? Task.FromResult<IReadOnlyList<ChangedFile>>(Files) : Task.FromException<IReadOnlyList<ChangedFile>>(ThrowOnFiles);
    }

    /// <summary>Head files given as bytes, for size and encoding cases; checked before <see cref="Contents"/>.</summary>
    public Dictionary<string, byte[]> RawContents { get; } = new(StringComparer.Ordinal);

    public Exception? ThrowOnBaseContent { get; set; }

    public Task<FileContent?> GetFileContentAsync(PullRequestRef pr, string path, CancellationToken cancellationToken)
    {
        Calls.Enqueue($"content {path}");
        return Task.FromResult(RawContents.TryGetValue(path, out var raw)
            ? FileContent.From(raw.Length, () => raw)
            : Contents.TryGetValue(path, out var text) ? FileContent.FromText(text) : null);
    }

    public Task<FileContent?> GetBaseFileContentAsync(PullRequestRef pr, string path, CancellationToken cancellationToken)
    {
        Calls.Enqueue($"base content {path}");
        if (ThrowOnBaseContent is not null)
        {
            return Task.FromException<FileContent?>(ThrowOnBaseContent);
        }

        return Task.FromResult(BaseContents.TryGetValue(path, out var text) ? FileContent.FromText(text) : null);
    }

    public Task<RepoTree?> GetTreeAsync(PullRequestRef pr, CancellationToken cancellationToken)
    {
        Calls.Enqueue("tree");
        return Task.FromResult(Paths is null ? null : new RepoTree(Paths, Symlinks));
    }

    public Task CompleteCheckRunAsync(PullRequestRef pr, long checkRunId, CheckRunReport report, CancellationToken cancellationToken)
    {
        Calls.Enqueue($"complete {checkRunId}");
        if (ThrowOnComplete is not null)
        {
            Completed.TrySetException(ThrowOnComplete);
            return Task.FromException(ThrowOnComplete);
        }

        Completed.TrySetResult(report);
        return Task.CompletedTask;
    }

    public Task<string> GetAppSlugAsync(CancellationToken cancellationToken)
    {
        Calls.Enqueue("slug");
        return Task.FromResult(Slug);
    }

    public Task<IReadOnlyList<IssueCommentInfo>> ListIssueCommentsAsync(PullRequestRef pr, CancellationToken cancellationToken)
    {
        Calls.Enqueue("list comments");
        return ForbidComments
            ? Task.FromException<IReadOnlyList<IssueCommentInfo>>(new GitHubForbiddenException("Resource not accessible by integration"))
            : Task.FromResult<IReadOnlyList<IssueCommentInfo>>(Comments.ToList());
    }

    public Task CreateIssueCommentAsync(PullRequestRef pr, string body, CancellationToken cancellationToken)
    {
        Calls.Enqueue("create comment");
        Comments.Add(new IssueCommentInfo(nextCommentId++, $"{Slug}[bot]", body, DateTimeOffset.UnixEpoch.AddSeconds(nextCommentId)));
        if (RacingCommentBody is not null)
        {
            Comments.Insert(Comments.Count - 1, new IssueCommentInfo(nextCommentId++, $"{Slug}[bot]", RacingCommentBody, DateTimeOffset.UnixEpoch));
            RacingCommentBody = null;
        }

        return Task.CompletedTask;
    }

    public Task UpdateIssueCommentAsync(PullRequestRef pr, long commentId, string body, CancellationToken cancellationToken)
    {
        Calls.Enqueue($"update comment {commentId}");
        var i = Comments.FindIndex(c => c.Id == commentId);
        Comments[i] = Comments[i] with { Body = body };
        return Task.CompletedTask;
    }

    public Task DeleteIssueCommentAsync(PullRequestRef pr, long commentId, CancellationToken cancellationToken)
    {
        Calls.Enqueue($"delete comment {commentId}");
        Comments.RemoveAll(c => c.Id == commentId);
        return Task.CompletedTask;
    }
}

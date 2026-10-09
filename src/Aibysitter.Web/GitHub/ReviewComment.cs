using System.Text;
using Aibysitter.Rules.PullRequests;

namespace Aibysitter.Web.GitHub;

public sealed record IssueCommentInfo(long Id, string Author, string Body, DateTimeOffset CreatedAt);

/// <summary>GitHub returned 403: the App lacks the permission for the call.</summary>
public sealed class GitHubForbiddenException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Body of the one PR comment posted when <see cref="RepoConfig.Comment"/> is set.</summary>
public static class ReviewComment
{
    public const string Marker = "<!-- aibysitter-review -->";
    public const int MaxFindings = 25;

    /// <summary>Below GitHub's 65,536-character comment limit.</summary>
    public const int MaxLength = 60_000;

    public static string CheckRunUrl(PullRequestRef pr, long checkRunId) => $"https://github.com/{pr.Owner}/{pr.Repo}/runs/{checkRunId}";

    public static string FileUrl(PullRequestRef pr, string path, int line) =>
        $"https://github.com/{pr.Owner}/{pr.Repo}/blob/{pr.HeadSha}/{string.Join('/', path.Split('/').Select(Uri.EscapeDataString))}#L{line}";

    public static string Build(CheckRunReport report, PullRequestRef pr, long checkRunId)
    {
        var findings = report.Annotations.Where(a => !a.IsConfigError).ToList();
        var listed = Math.Min(MaxFindings, findings.Count);
        while (true)
        {
            var body = Compose(report, findings, pr, checkRunId, listed);
            if (body.Length <= MaxLength)
            {
                return body;
            }

            if (listed == 0)
            {
                var link = $"\n\n[View the check run]({CheckRunUrl(pr, checkRunId)})";
                return CutAtLineEnd(body, MaxLength - link.Length - 2) + "\n…" + link;
            }

            listed--;
        }
    }

    /// <summary>At most <paramref name="max"/> characters, ending at a line end so no code span or surrogate pair is split.</summary>
    private static string CutAtLineEnd(string text, int max)
    {
        var cut = text.LastIndexOf('\n', max);
        return cut > 0 ? text[..cut] : OctokitGitHubGateway.Clamp(text, max);
    }

    private static string Compose(CheckRunReport report, IReadOnlyList<CheckRunAnnotation> findings, PullRequestRef pr, long checkRunId, int listed)
    {
        var text = new StringBuilder();
        text.Append(Marker).Append('\n');
        text.Append($"**Aibysitter review: {report.Title}** · commit `{pr.HeadSha[..Math.Min(7, pr.HeadSha.Length)]}`\n\n");
        text.Append(report.Summary).Append('\n');

        if (findings.Count > 0)
        {
            text.Append("\nFindings:\n\n");
            foreach (var a in findings.Take(listed))
            {
                text.Append($"- [{GitHubMarkdown.Code($"{a.Path}:{a.Line}")}]({FileUrl(pr, a.Path, a.Line)}) {a.Title}: {GitHubMarkdown.Text(a.Message)}\n");
            }

            var more = findings.Count - listed;
            if (more > 0)
            {
                text.Append($"\nand {more} more in the check run.\n");
            }
        }

        text.Append($"\n[View the check run]({CheckRunUrl(pr, checkRunId)})\n");
        return text.ToString();
    }
}

/// <summary>Creates or updates the one review comment on a PR; removes duplicates left by overlapping processes.</summary>
public sealed class ReviewCommentPublisher(IGitHubGateway gateway, ILogger logger)
{
    public const string PermissionNote = "PR comment not posted: the App needs Pull requests: Read and write; accept the permission request in the installation settings.";
    public const string ErrorNote = "PR comment not posted: GitHub returned an error. It is retried on the next commit.";

    /// <param name="hasFindings">False: no comment is created, but an existing one is updated.</param>
    /// <returns>A note for the check summary when the comment was not posted; otherwise null.</returns>
    public async Task<string?> PublishAsync(PullRequestRef pr, string body, bool hasFindings, CancellationToken cancellationToken)
    {
        try
        {
            var slug = await gateway.GetAppSlugAsync(cancellationToken);
            var ours = await OursAsync(pr, slug, cancellationToken);
            if (ours.Count == 0)
            {
                if (!hasFindings)
                {
                    return null;
                }

                await gateway.CreateIssueCommentAsync(pr, body, cancellationToken);
                ours = await OursAsync(pr, slug, cancellationToken);
                if (ours.Count <= 1)
                {
                    return null;
                }
            }

            await gateway.UpdateIssueCommentAsync(pr, ours[0].Id, body, cancellationToken);
            foreach (var extra in ours.Skip(1))
            {
                await gateway.DeleteIssueCommentAsync(pr, extra.Id, cancellationToken);
            }

            return null;
        }
        catch (GitHubForbiddenException ex)
        {
            logger.LogWarning(ex, "PR comment on {PullRequest} not posted: permission denied", pr);
            return PermissionNote;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "PR comment on {PullRequest} not posted", pr);
            return ErrorNote;
        }
    }

    private async Task<List<IssueCommentInfo>> OursAsync(PullRequestRef pr, string slug, CancellationToken cancellationToken) =>
        (await gateway.ListIssueCommentsAsync(pr, cancellationToken))
            .Where(c => c.Author == $"{slug}[bot]" && c.Body.StartsWith(ReviewComment.Marker, StringComparison.Ordinal))
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .ToList();
}

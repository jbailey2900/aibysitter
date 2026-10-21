using System.Text.Json;

namespace Aibysitter.Web.GitHub;

public static class WebhookPayload
{
    public static readonly IReadOnlySet<string> ReviewedActions = new HashSet<string>(StringComparer.Ordinal) { "opened", "synchronize", "reopened" };

    /// <summary>Also reviewed: <c>edited</c> when the description changed (<c>changes.body</c>), so a P008 justification applies without a push.</summary>
    public const string EditedAction = "edited";

    /// <summary>True for <see cref="ReviewedActions"/>, and for <see cref="EditedAction"/> with a description change.</summary>
    public static bool IsReviewed(string action, bool descriptionChanged) =>
        ReviewedActions.Contains(action) || (action == EditedAction && descriptionChanged);

    /// <summary>Reads action and PR reference from a pull_request event body. Returns false when required fields are missing.</summary>
    public static bool TryParsePullRequest(ReadOnlySpan<byte> body, out string action, out PullRequestRef? pullRequest) =>
        TryParsePullRequest(body, out action, out pullRequest, out _);

    /// <param name="descriptionChanged">The payload has <c>changes.body</c> (an <c>edited</c> event that changed the description).</param>
    public static bool TryParsePullRequest(ReadOnlySpan<byte> body, out string action, out PullRequestRef? pullRequest, out bool descriptionChanged)
    {
        action = string.Empty;
        pullRequest = null;
        descriptionChanged = false;

        try
        {
            var reader = new Utf8JsonReader(body);
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;

            action = root.GetProperty("action").GetString() ?? string.Empty;
            descriptionChanged = root.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Object && changes.TryGetProperty("body", out _);
            pullRequest = new PullRequestRef(
                root.GetProperty("installation").GetProperty("id").GetInt64(),
                root.GetProperty("repository").GetProperty("owner").GetProperty("login").GetString()!,
                root.GetProperty("repository").GetProperty("name").GetString()!,
                root.GetProperty("pull_request").GetProperty("number").GetInt32(),
                root.GetProperty("pull_request").GetProperty("head").GetProperty("sha").GetString()!,
                root.GetProperty("pull_request").TryGetProperty("base", out var baseRef) && baseRef.TryGetProperty("sha", out var baseSha) ? baseSha.GetString() : null);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }
}

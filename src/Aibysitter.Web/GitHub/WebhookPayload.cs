using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aibysitter.Web.GitHub;

public static partial class WebhookPayload
{
    public static readonly IReadOnlySet<string> ReviewedActions = new HashSet<string>(StringComparer.Ordinal) { "opened", "synchronize", "reopened" };

    /// <summary>Also reviewed: <c>edited</c> on an open pull request when the description's <c>Aibysitter:</c> lines changed, so a P008 justification applies without a push.</summary>
    public const string EditedAction = "edited";

    /// <summary>True for <see cref="ReviewedActions"/>, and for <see cref="EditedAction"/> when <paramref name="justificationsChanged"/>.</summary>
    public static bool IsReviewed(string action, bool justificationsChanged) =>
        ReviewedActions.Contains(action) || (action == EditedAction && justificationsChanged);

    /// <summary>Reads action and PR reference from a pull_request event body. Returns false when required fields are missing.</summary>
    public static bool TryParsePullRequest(ReadOnlySpan<byte> body, out string action, out PullRequestRef? pullRequest) =>
        TryParsePullRequest(body, out action, out pullRequest, out _);

    /// <param name="justificationsChanged">The payload has <c>changes.body</c>, the pull request is open, and its <c>Aibysitter:</c> lines differ from <c>changes.body.from</c>.</param>
    public static bool TryParsePullRequest(ReadOnlySpan<byte> body, out string action, out PullRequestRef? pullRequest, out bool justificationsChanged)
    {
        action = string.Empty;
        pullRequest = null;
        justificationsChanged = false;

        try
        {
            var reader = new Utf8JsonReader(body);
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;

            action = root.GetProperty("action").GetString() ?? string.Empty;
            var pr = root.GetProperty("pull_request");
            justificationsChanged = root.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Object
                && changes.TryGetProperty("body", out var bodyChange)
                && pr.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String && state.GetString() == "open"
                && !AibysitterLines(StringOrNull(bodyChange, "from")).SequenceEqual(AibysitterLines(StringOrNull(pr, "body")), StringComparer.Ordinal);
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

    private static string? StringOrNull(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Lines starting with <c>Aibysitter:</c> after leading whitespace, trimmed, in order.</summary>
    private static IEnumerable<string> AibysitterLines(string? text) =>
        text is null ? [] : text.Replace("\r\n", "\n").Split('\n').Where(l => AibysitterLineRegex().IsMatch(l)).Select(l => l.Trim());

    [GeneratedRegex(@"^[ \t]*Aibysitter:")]
    private static partial Regex AibysitterLineRegex();
}

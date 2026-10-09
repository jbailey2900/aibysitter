using Aibysitter.Rules;
using Aibysitter.Web.Data;
using Aibysitter.Web.Gallery;
using Microsoft.Extensions.Caching.Memory;

namespace Aibysitter.Web.Linting;

/// <summary>
/// <c>GET /badge/{owner}/{repo}.svg[?file=NAME]</c>: lint score of a public repository's rules file, default branch.
/// Results are cached; a cache miss fetches, lints and stores one score history row when a file is found.
/// </summary>
public static class BadgeEndpoints
{
    public const string Prefix = "/badge/";
    public const string FoundCacheControl = "public, max-age=3600";
    public const string TransientCacheControl = "public, max-age=300";
    public const string BusyCacheControl = "no-store";

    public static string Path(RepoRef repo, string? file) =>
        $"{Prefix}{ScoreHistoryKey.Repo(repo)}.svg" + (file is null ? "" : "?file=" + Uri.EscapeDataString(file));

    public static IEndpointRouteBuilder MapBadges(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Prefix + "{owner}/{repo}.svg", HandleAsync);
        return endpoints;
    }

    internal static async Task<IResult> HandleAsync(string owner, string repo, HttpContext context, BadgeService badges, CancellationToken cancellationToken)
    {
        var file = context.Request.Query["file"].ToString();
        if (!RepoInput.TryParse($"{owner}/{repo}", out var parsed) || parsed!.HasExtraPath
            || (file.Length > 0 && !RawGitHubFetcher.FileNames.Contains(file)))
        {
            return Svg(context, ScoreBadge.RenderUnknown(), TransientCacheControl, StatusCodes.Status404NotFound);
        }

        var result = await badges.GetAsync(parsed, file.Length == 0 ? null : file, cancellationToken);
        return result switch
        {
            { Score: { } score, Grade: { } grade } => Svg(context, ScoreBadge.Render(score, grade), FoundCacheControl),
            { Busy: true } => Svg(context, ScoreBadge.RenderUnknown(), BusyCacheControl),
            { Transient: true } => Svg(context, ScoreBadge.RenderUnknown(), TransientCacheControl),
            _ => Svg(context, ScoreBadge.RenderUnknown(), FoundCacheControl),
        };
    }

    private static IResult Svg(HttpContext context, string svg, string cacheControl, int status = StatusCodes.Status200OK)
    {
        context.Response.Headers.CacheControl = cacheControl;
        return Results.Content(svg, "image/svg+xml; charset=utf-8", System.Text.Encoding.UTF8, status);
    }
}

/// <param name="Transient">GitHub timed out or was unreachable; cached briefly.</param>
/// <param name="Busy">Too many lookups were already fetching; not cached, sent with no-store.</param>
public sealed record BadgeResult(int? Score, string? Grade, bool Transient = false, bool Busy = false);

/// <summary>Badge lookups with a size-limited cache: found and not-found for one hour, transient failures for five minutes.</summary>
/// <remarks>The typed <see cref="RawGitHubFetcher"/> is resolved per lookup so its HttpClient handler rotates.</remarks>
public sealed class BadgeService(IServiceProvider services, LintService lint, IScoreHistory history, Stats.IUsageCounter? usage = null) : IDisposable
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromHours(1);
    public static readonly TimeSpan TransientCacheFor = TimeSpan.FromMinutes(5);
    public const int MaxEntries = 10_000;

    /// <summary>Cache misses fetching at once, across all keys. Beyond it a lookup answers <see cref="BadgeResult.Busy"/>.</summary>
    public const int MaxConcurrentMisses = 8;

    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = MaxEntries });
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<BadgeResult>>> inFlight = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim misses = new(MaxConcurrentMisses, MaxConcurrentMisses);

    public async Task<BadgeResult> GetAsync(RepoRef repo, string? file, CancellationToken cancellationToken)
    {
        var key = $"{ScoreHistoryKey.Repo(repo)}|{file}";
        if (!cache.TryGetValue(key, out BadgeResult? result))
        {
            // One fetch per key: concurrent misses share it. It runs on its own budget, not a caller's token.
            var miss = inFlight.GetOrAdd(key, k => new Lazy<Task<BadgeResult>>(() => MissAsync(k, repo, file)));
            result = await miss.Value.WaitAsync(cancellationToken);
        }

        usage?.Increment(Stats.UsageMetric.Badge, OutcomeKey(result!));
        return result!;
    }

    private async Task<BadgeResult> MissAsync(string key, RepoRef repo, string? file)
    {
        try
        {
            if (!await misses.WaitAsync(0))
            {
                return new BadgeResult(null, null, Busy: true);
            }

            try
            {
                var result = await LintAsync(repo, file, CancellationToken.None);
                cache.Set(key, result, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = result.Transient ? TransientCacheFor : CacheFor });
                return result;
            }
            finally
            {
                misses.Release();
            }
        }
        finally
        {
            inFlight.TryRemove(key, out _);
        }
    }

    private async Task<BadgeResult> LintAsync(RepoRef repo, string? file, CancellationToken cancellationToken)
    {
        var fetched = await services.GetRequiredService<RawGitHubFetcher>().FetchAsync(repo, file, cancellationToken);
        if (fetched.Status is FetchStatus.TimedOut or FetchStatus.Unreachable)
        {
            return new BadgeResult(null, null, Transient: true);
        }

        if (fetched.Status != FetchStatus.Found || (file is not null && fetched.FileName != file))
        {
            return new BadgeResult(null, null);
        }

        LintOutcome outcome;
        try
        {
            outcome = lint.Lint(fetched.Content!, RulesFormats.FromFileName(fetched.FileName!) ?? RulesFormat.Auto, [], "badge");
        }
        catch (LintTimeoutException)
        {
            return new BadgeResult(null, null);
        }

        if (history.Enabled)
        {
            await history.RecordAsync(
                new ScoreRecord(ScoreHistoryKey.Repo(repo), fetched.FileName!, RulesetVersion.Current, outcome.Score.Value, outcome.Score.Grade, ScoreSource.Badge),
                cancellationToken);
        }

        return new BadgeResult(outcome.Score.Value, outcome.Score.Grade);
    }

    public static string OutcomeKey(BadgeResult result) =>
        result.Score is not null ? "scored" : result.Busy ? "busy" : result.Transient ? "unavailable" : "not-found";

    public void Dispose()
    {
        cache.Dispose();
        misses.Dispose();
    }
}

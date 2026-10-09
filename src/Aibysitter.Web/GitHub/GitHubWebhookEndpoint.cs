using Microsoft.Extensions.Options;

namespace Aibysitter.Web.GitHub;

public static class GitHubWebhookEndpoint
{
    public const string Path = "/github/webhook";
    /// <summary>Pull request payloads are far smaller; GitHub's own cap is 25 MB.</summary>
    public const int MaxBodyBytes = 1024 * 1024;

    public static IEndpointRouteBuilder MapGitHubWebhook(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(Path, HandleAsync).DisableAntiforgery();
        return endpoints;
    }

    internal static async Task<IResult> HandleAsync(
        HttpRequest request,
        IOptions<GitHubOptions> options,
        IGitHubGateway gateway,
        ReviewQueue queue,
        IReviewJobStore store,
        WebhookSignatureLimiter limiter,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(typeof(GitHubWebhookEndpoint));
        var settings = options.Value;
        if (!settings.IsConfigured)
        {
            logger.LogWarning("GitHub webhook received but the GitHub App is not configured");
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var client = Infrastructure.ClientKey.For(request.HttpContext.Connection.RemoteIpAddress);
        if (limiter.IsExhausted(client))
        {
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }

        var body = await Infrastructure.RequestBody.ReadCappedAsync(request, MaxBodyBytes, cancellationToken);
        if (body is null)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        if (!WebhookSignature.IsValid(settings.WebhookSecret!, body, request.Headers[WebhookSignature.HeaderName]))
        {
            if (!limiter.TryCount(client))
            {
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }

            logger.LogWarning("GitHub webhook rejected: invalid signature");
            return Results.Unauthorized();
        }

        var eventName = request.Headers["X-GitHub-Event"].ToString();
        var deliveryId = request.Headers["X-GitHub-Delivery"].ToString();

        if (eventName == "ping")
        {
            return Results.Ok("pong");
        }

        if (eventName != "pull_request")
        {
            return Results.NoContent();
        }

        if (!WebhookPayload.TryParsePullRequest(body, out var action, out var pr))
        {
            logger.LogWarning("GitHub delivery {DeliveryId}: pull_request payload missing required fields", deliveryId);
            return Results.BadRequest();
        }

        if (!WebhookPayload.ReviewedActions.Contains(action))
        {
            return Results.NoContent();
        }

        return store.Find(deliveryId, out var existing) switch
        {
            StoredJobState.InProgress => InProgress(deliveryId, logger),
            StoredJobState.Pending => Requeue(existing!, queue, logger),
            _ => await QueueNewAsync(pr!, deliveryId, gateway, store, queue, logger, cancellationToken),
        };
    }

    private static IResult InProgress(string deliveryId, ILogger logger)
    {
        logger.LogInformation("GitHub delivery {DeliveryId}: review already in progress", deliveryId);
        return Results.Accepted();
    }

    /// <summary>Redelivery of a saved job: queue it again against its existing check run.</summary>
    private static IResult Requeue(ReviewJob job, ReviewQueue queue, ILogger logger)
    {
        if (!queue.TryEnqueue(job))
        {
            logger.LogError("GitHub delivery {DeliveryId}: review queue full; saved job kept for the next start", job.DeliveryId);
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        logger.LogInformation("GitHub delivery {DeliveryId}: re-queued review of {PullRequest} as check run {CheckRunId}", job.DeliveryId, job.PullRequest, job.CheckRunId);
        return Results.Accepted();
    }

    private static async Task<IResult> QueueNewAsync(
        PullRequestRef pr,
        string deliveryId,
        IGitHubGateway gateway,
        IReviewJobStore store,
        ReviewQueue queue,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        long checkRunId;
        try
        {
            checkRunId = await gateway.CreateQueuedCheckRunAsync(pr, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GitHub delivery {DeliveryId}: could not create queued check run for {PullRequest}", deliveryId, pr);
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }

        var job = new ReviewJob(pr, checkRunId, JobDeliveryId(deliveryId, store, logger));
        try
        {
            store.Save(job);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "GitHub delivery {DeliveryId}: could not save review job; closing check run {CheckRunId}", deliveryId, checkRunId);
            return await CloseUnavailableAsync(job, gateway, "Review job could not be saved", logger, cancellationToken);
        }

        if (!queue.TryEnqueue(job))
        {
            logger.LogError("GitHub delivery {DeliveryId}: review queue full; closing check run {CheckRunId}", deliveryId, checkRunId);
            store.TryAcquire(job)?.Complete();
            return await CloseUnavailableAsync(job, gateway, "Review queue full", logger, cancellationToken);
        }

        logger.LogInformation("GitHub delivery {DeliveryId}: queued review of {PullRequest} as check run {CheckRunId}", deliveryId, pr, checkRunId);
        return Results.Accepted();
    }

    /// <summary>File names need a GUID; GitHub always sends one. A missing or malformed header gets a new one.</summary>
    private static string JobDeliveryId(string deliveryId, IReviewJobStore store, ILogger logger)
    {
        if (!store.IsDurable || Guid.TryParse(deliveryId, out _))
        {
            return deliveryId;
        }

        var generated = Guid.NewGuid().ToString("D");
        logger.LogWarning("GitHub delivery ID \"{DeliveryId}\" is not a GUID; stored as {Generated}", deliveryId, generated);
        return generated;
    }

    private static async Task<IResult> CloseUnavailableAsync(ReviewJob job, IGitHubGateway gateway, string reason, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            await gateway.CompleteCheckRunAsync(job.PullRequest, job.CheckRunId, CheckRunReport.ForError(new InvalidOperationException(reason)), cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GitHub delivery {DeliveryId}: could not close check run {CheckRunId}", job.DeliveryId, job.CheckRunId);
        }

        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
}

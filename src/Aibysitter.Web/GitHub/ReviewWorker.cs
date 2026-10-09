namespace Aibysitter.Web.GitHub;

/// <summary>
/// Reviews queued jobs one at a time. Each review holds the job's lease; the job is removed once its check run is
/// completed and kept when shutdown interrupts it. After <see cref="IReviewJobStore.MaxAttempts"/> interrupted
/// attempts the check run is closed with an error instead. A review whose check run could not be closed keeps its job
/// and is queued again after <see cref="RetryDelay"/> (durable store only).
/// </summary>
public sealed class ReviewWorker(
    ReviewQueue queue,
    ReviewProcessor processor,
    IReviewJobStore store,
    IGitHubGateway gateway,
    TimeProvider time,
    ILogger<ReviewWorker> logger) : BackgroundService
{
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await RunAsync(job, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                // A job store error (permissions, a locked file) must not stop the worker; the job file is kept for the next start.
                logger.LogError(ex, "Review job {DeliveryId} for {PullRequest} failed outside the review; worker continues", job.DeliveryId, job.PullRequest);
            }
        }
    }

    internal async Task RunAsync(ReviewJob job, CancellationToken cancellationToken)
    {
        using var lease = store.TryAcquire(job);
        if (lease is null)
        {
            logger.LogInformation("Review of {PullRequest} (delivery {DeliveryId}) skipped: completed or claimed by another process", job.PullRequest, job.DeliveryId);
            return;
        }

        if (lease.Attempts > IReviewJobStore.MaxAttempts)
        {
            await AbandonAsync(job, cancellationToken);
        }
        else if (!await processor.ProcessAsync(job, cancellationToken) && store.IsDurable)
        {
            logger.LogWarning(
                "Check run {CheckRunId} for {PullRequest} not closed; job kept, retry in {Delay} (attempt {Attempt} of {MaxAttempts})",
                job.CheckRunId, job.PullRequest, RetryDelay, lease.Attempts, IReviewJobStore.MaxAttempts);
            RequeueLater(job, cancellationToken);
            return;
        }

        lease.Complete();
    }

    /// <summary>The returned task is for tests; the worker does not wait for it.</summary>
    internal Task? PendingRetry { get; private set; }

    private void RequeueLater(ReviewJob job, CancellationToken cancellationToken) =>
        PendingRetry = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(RetryDelay, time, cancellationToken);
                if (!queue.TryEnqueue(job))
                {
                    logger.LogError("Review queue full; saved job {DeliveryId} kept for the next start", job.DeliveryId);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutdown: the saved job is recovered on the next start.
            }
        }, CancellationToken.None);

    private async Task AbandonAsync(ReviewJob job, CancellationToken cancellationToken)
    {
        logger.LogError("Review of {PullRequest} (delivery {DeliveryId}) interrupted {MaxAttempts} times; closing check run {CheckRunId}", job.PullRequest, job.DeliveryId, IReviewJobStore.MaxAttempts, job.CheckRunId);
        try
        {
            var error = new InvalidOperationException($"Review interrupted {IReviewJobStore.MaxAttempts} times");
            await gateway.CompleteCheckRunAsync(job.PullRequest, job.CheckRunId, CheckRunReport.ForError(error), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Could not close check run {CheckRunId} for {PullRequest}", job.CheckRunId, job.PullRequest);
        }
    }
}

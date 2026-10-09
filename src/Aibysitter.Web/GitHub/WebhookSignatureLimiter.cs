using System.Threading.RateLimiting;
using Aibysitter.Web.Infrastructure;

namespace Aibysitter.Web.GitHub;

/// <summary>
/// Counts webhook requests that fail the signature check, per client IP. Signed deliveries are never counted, so GitHub's
/// own traffic from a few hook IPs is not throttled. An IP over the limit gets 429 before its body is read.
/// </summary>
public sealed class WebhookSignatureLimiter(WebhookRateLimitSettings settings) : IDisposable
{
    private readonly PartitionedRateLimiter<string> limiter = PartitionedRateLimiter.Create<string, string>(ip =>
        RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = settings.PermitLimit,
            Window = TimeSpan.FromSeconds(settings.WindowSeconds),
            QueueLimit = 0,
        }));

    public bool IsExhausted(string ip) => limiter.GetStatistics(ip)?.CurrentAvailablePermits == 0;

    /// <summary>Counts one failed signature; false when the IP was already over the limit.</summary>
    public bool TryCount(string ip)
    {
        using var lease = limiter.AttemptAcquire(ip);
        return lease.IsAcquired;
    }

    public void Dispose() => limiter.Dispose();
}

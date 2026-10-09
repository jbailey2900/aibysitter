using System.Collections.Concurrent;
using Microsoft.Playwright;

namespace Aibysitter.Web.Smoke;

/// <summary>Aborts every request whose host is not localhost and records it.</summary>
public sealed class NetworkGuard
{
    public ConcurrentQueue<string> Blocked { get; } = new();

    public static async Task<NetworkGuard> InstallAsync(IBrowserContext context)
    {
        var guard = new NetworkGuard();
        await context.RouteAsync("**/*", async route =>
        {
            var url = new Uri(route.Request.Url);
            if (url.Scheme is "data" or "blob" || url.Host is "127.0.0.1" or "localhost")
            {
                await route.ContinueAsync();
                return;
            }

            guard.Blocked.Enqueue(route.Request.Url);
            await route.AbortAsync();
        });
        return guard;
    }
}

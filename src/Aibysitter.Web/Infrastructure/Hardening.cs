using System.Threading.RateLimiting;
using Aibysitter.Web.Linting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

namespace Aibysitter.Web.Infrastructure;

public static class Hardening
{
    public const string ContentSecurityPolicy =
        "default-src 'self'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'; object-src 'none'";

    public static IServiceCollection AddAibysitterHardening(this IServiceCollection services, IConfiguration configuration)
    {
        var forwarded = configuration.GetSection(ForwardedHeadersSettings.SectionName).Get<ForwardedHeadersSettings>()
            ?? new ForwardedHeadersSettings();

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            foreach (var cidr in forwarded.KnownNetworks)
            {
                options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
            }
        });

        var lint = configuration.GetSection(LintRateLimitSettings.SectionName).Get<LintRateLimitSettings>()
            ?? new LintRateLimitSettings();

        var badge = configuration.GetSection(BadgeRateLimitSettings.SectionName).Get<BadgeRateLimitSettings>()
            ?? new BadgeRateLimitSettings();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, cancellationToken) => WriteRejectionAsync(context, lint, cancellationToken);
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                IsLintRequest(context.Request)
                    ? RateLimitPartition.GetFixedWindowLimiter(
                        ClientKey.For(context.Connection.RemoteIpAddress),
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = lint.PermitLimit,
                            Window = TimeSpan.FromSeconds(lint.WindowSeconds),
                            QueueLimit = 0,
                        })
                    : IsBadgeRequest(context.Request)
                        ? RateLimitPartition.GetFixedWindowLimiter(
                            "badge|" + ClientKey.For(context.Connection.RemoteIpAddress),
                            _ => new FixedWindowRateLimiterOptions
                            {
                                PermitLimit = badge.PermitLimit,
                                Window = TimeSpan.FromSeconds(badge.WindowSeconds),
                                QueueLimit = 0,
                            })
                        : RateLimitPartition.GetNoLimiter(string.Empty));
        });

        return services;
    }

    /// <summary>Form posts and API calls share one bucket per client IP. Segment match, so /Lint/ and /api/lint/ count too.</summary>
    private static bool IsLintRequest(HttpRequest request) =>
        HttpMethods.IsPost(request.Method) && (IsFormPath(request) || IsApiPath(request));

    private static bool IsFormPath(HttpRequest request) => request.Path.StartsWithSegments("/Lint", StringComparison.OrdinalIgnoreCase);

    private static bool IsApiPath(HttpRequest request) => request.Path.StartsWithSegments(LintApi.Path, StringComparison.OrdinalIgnoreCase);

    /// <summary>Badges: one bucket per client IP, separate from linting.</summary>
    private static bool IsBadgeRequest(HttpRequest request) =>
        HttpMethods.IsGet(request.Method) && request.Path.StartsWithSegments(BadgeEndpoints.Prefix.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Form posts and API calls get no-store and Retry-After. Form posts get a text body with the limit and retry window;
    /// API callers get ProblemDetails. Badges keep the bare 429.
    /// </summary>
    private static ValueTask WriteRejectionAsync(OnRejectedContext context, LintRateLimitSettings lint, CancellationToken cancellationToken)
    {
        var request = context.HttpContext.Request;
        var response = context.HttpContext.Response;
        var isForm = HttpMethods.IsPost(request.Method) && IsFormPath(request);
        var isApi = IsApiPath(request);
        if (!isForm && !isApi)
        {
            return ValueTask.CompletedTask;
        }

        response.Headers.CacheControl = "no-store";
        var retry = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var after)
            ? (int?)Math.Max(1, (int)Math.Ceiling(after.TotalSeconds))
            : null;
        if (retry is { } seconds)
        {
            response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (isForm)
        {
            response.ContentType = "text/plain; charset=utf-8";
            return new ValueTask(response.WriteAsync(FormRejectionText(lint, retry), cancellationToken));
        }

        return new ValueTask(response.WriteAsJsonAsync(
            new Microsoft.AspNetCore.Mvc.ProblemDetails { Status = StatusCodes.Status429TooManyRequests, Title = "Too many requests. Try again in a minute." },
            options: null,
            contentType: "application/problem+json",
            cancellationToken));
    }

    internal static string FormRejectionText(LintRateLimitSettings lint, int? retrySeconds) =>
        $"Too many lint requests. Limit: {lint.PermitLimit} per {lint.WindowSeconds} seconds. "
        + (retrySeconds is { } seconds ? $"Try again in {seconds} seconds." : "Try again in a minute.");

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers.XContentTypeOptions = "nosniff";
                headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
                headers.ContentSecurityPolicy = ContentSecurityPolicy;
                return Task.CompletedTask;
            });

            await next(context);
        });
}

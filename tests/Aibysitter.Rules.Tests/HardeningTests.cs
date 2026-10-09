using System.Net;
using Aibysitter.Web.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Aibysitter.Rules.Tests;

public class HardeningTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private const string TestRemoteIpHeader = "X-Test-Remote-IP";
    private const string CloudflareIp = "104.16.0.1";
    private const string NonCloudflareIp = "198.51.100.7";

    [Fact]
    public async Task Responses_IncludeSecurityHeaders()
    {
        var response = await factory.CreateClient().GetAsync("/");

        response.EnsureSuccessStatusCode();
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("strict-origin-when-cross-origin", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.Equal(Hardening.ContentSecurityPolicy, Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
    }

    [Fact]
    public async Task LintPost_OverLimit_Returns429_AndGetIsNotLimited()
    {
        var client = CreateClient(permitLimit: 2);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await PostLint(client, NonCloudflareIp)).StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await PostLint(client, NonCloudflareIp)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostLint(client, NonCloudflareIp)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Lint")).StatusCode);
    }

    [Fact]
    public async Task FormAndApi_ShareOneBucket_ApiRejectionIsProblemNoStore()
    {
        var client = CreateClient(permitLimit: 2);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await PostLint(client, NonCloudflareIp)).StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await PostApi(client, NonCloudflareIp)).StatusCode);
        var rejected = await PostApi(client, NonCloudflareIp);

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", rejected.Headers.CacheControl?.ToString());
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostLint(client, NonCloudflareIp)).StatusCode);
    }

    [Fact]
    public async Task FormRejection_IsTextWithRetryWindow_NoStore()
    {
        var client = CreateClient(permitLimit: 1);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await PostLint(client, NonCloudflareIp)).StatusCode);
        var rejected = await PostLint(client, NonCloudflareIp);
        var body = await rejected.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("text/plain", rejected.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", rejected.Content.Headers.ContentType?.CharSet);
        Assert.Equal("no-store", rejected.Headers.CacheControl?.ToString());
        var retry = rejected.Headers.RetryAfter?.Delta;
        Assert.NotNull(retry);
        Assert.InRange(retry!.Value.TotalSeconds, 1, 60);
        Assert.Equal($"Too many lint requests. Limit: 1 per 60 seconds. Try again in {(int)retry.Value.TotalSeconds} seconds.", body);

        var api = await PostApi(client, NonCloudflareIp);
        Assert.Equal("application/problem+json", api.Content.Headers.ContentType?.MediaType);
        Assert.InRange(api.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 1, 60);
        Assert.Equal("no-store", api.Headers.CacheControl?.ToString());
        using var problem = System.Text.Json.JsonDocument.Parse(await api.Content.ReadAsStringAsync());
        Assert.Equal("Too many requests. Try again in a minute.", problem.RootElement.GetProperty("title").GetString());
        Assert.Equal(429, problem.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public void FormRejectionText_WithoutRetryMetadata_SaysAMinute() =>
        Assert.Equal(
            "Too many lint requests. Limit: 20 per 60 seconds. Try again in a minute.",
            Hardening.FormRejectionText(new Web.Infrastructure.LintRateLimitSettings(), null));

    /// <summary>Security review item 9: a trailing slash does not leave the bucket.</summary>
    [Theory]
    [InlineData("/Lint/")]
    [InlineData("/Lint/?handler=Url")]
    [InlineData("/api/lint/")]
    public async Task TrailingSlashPaths_ShareTheBucket(string path)
    {
        var client = CreateClient(permitLimit: 1);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await PostLint(client, NonCloudflareIp)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostLint(client, NonCloudflareIp, path: path)).StatusCode);
    }

    [Fact]
    public async Task Ipv6_OneBucketPer64()
    {
        var client = CreateClient(permitLimit: 1);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await PostLint(client, "2001:db8:1:2::1")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostLint(client, "2001:db8:1:2:ffff:ffff:ffff:fffe")).StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await PostLint(client, "2001:db8:1:3::1")).StatusCode);
    }

    [Fact]
    public async Task Ipv6_WebhookFailedSignatures_OneBucketPer64()
    {
        var (client, _) = GitHub.GitHubWebhookTests.Create(
            factory.WithWebHostBuilder(b => b
                .UseSetting("RateLimiting:Webhook:PermitLimit", "1")
                .ConfigureServices(s => s.AddTransient<IStartupFilter, TestRemoteIpStartupFilter>())),
            new GitHub.FakeGitHubGateway(), runWorker: false);

        Task<HttpResponseMessage> BadPing(string ip)
        {
            var request = GitHub.WebhookTestData.Request("ping", "{}", signature: "sha256=00");
            request.Headers.Add(TestRemoteIpHeader, ip);
            return client.SendAsync(request);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await BadPing("2001:db8:1:2::1")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await BadPing("2001:db8:1:2::2")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await BadPing("2001:db8:1:3::1")).StatusCode);
    }

    [Fact]
    public async Task UrlLint_SharesTheBucket()
    {
        var client = CreateClient(permitLimit: 2);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await PostLint(client, NonCloudflareIp)).StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await PostLint(client, NonCloudflareIp, path: "/Lint?handler=Url")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostApi(client, NonCloudflareIp)).StatusCode);
    }

    [Fact]
    public async Task ForwardedFor_FromCloudflare_PartitionsByClient()
    {
        var client = CreateClient(permitLimit: 1);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await PostLint(client, CloudflareIp, "203.0.113.1")).StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await PostLint(client, CloudflareIp, "203.0.113.2")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostLint(client, CloudflareIp, "203.0.113.1")).StatusCode);
    }

    [Fact]
    public async Task ForwardedFor_FromUnknownProxy_IsIgnored()
    {
        var client = CreateClient(permitLimit: 1);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await PostLint(client, NonCloudflareIp, "203.0.113.1")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostLint(client, NonCloudflareIp, "203.0.113.2")).StatusCode);
    }

    [Theory]
    [InlineData("aibysitting.net", HttpStatusCode.OK)]
    [InlineData("www.aibysitting.net", HttpStatusCode.OK)]
    [InlineData("evil.example", HttpStatusCode.BadRequest)]
    public async Task Production_FiltersHosts(string host, HttpStatusCode expected)
    {
        var logPath = Path.Combine(Path.GetTempPath(), "aibysitter-tests", "log-.txt");
        var client = factory
            .WithWebHostBuilder(b => b
                .UseEnvironment("Production")
                .UseSetting("Serilog:WriteTo:0:Args:path", logPath))
            .CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Host = host;

        Assert.Equal(expected, (await client.SendAsync(request)).StatusCode);
    }

    private HttpClient CreateClient(int permitLimit) =>
        factory
            .WithWebHostBuilder(b => b
                .UseSetting("RateLimiting:Lint:PermitLimit", permitLimit.ToString())
                .ConfigureServices(s => s.AddTransient<IStartupFilter, TestRemoteIpStartupFilter>()))
            .CreateClient();

    private static Task<HttpResponseMessage> PostLint(HttpClient client, string remoteIp, string? forwardedFor = null, string path = "/Lint")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["RulesText"] = "x" }),
        };
        request.Headers.Add(TestRemoteIpHeader, remoteIp);
        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> PostApi(HttpClient client, string remoteIp)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Web.Linting.LintApi.Path)
        {
            Content = new StringContent("{\"content\": \"x\"}", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(TestRemoteIpHeader, remoteIp);
        return client.SendAsync(request);
    }

    private sealed class TestRemoteIpStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue(TestRemoteIpHeader, out var ip))
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse(ip.ToString());
                }

                await nextMiddleware(context);
            });
            next(app);
        };
    }
}

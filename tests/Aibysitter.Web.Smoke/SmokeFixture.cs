using Microsoft.Playwright;

namespace Aibysitter.Web.Smoke;

/// <summary>
/// Browser and base URL for the smoke suite. <c>AIBYSITTER_SMOKE_URL</c> is the running site; without it every smoke
/// test is skipped. <c>AIBYSITTER_SMOKE_BROWSER</c> is a Chromium executable; without it the <c>chrome</c> channel is used.
/// </summary>
public sealed class SmokeFixture : IAsyncLifetime
{
    public const string UrlVariable = "AIBYSITTER_SMOKE_URL";
    public const string BrowserVariable = "AIBYSITTER_SMOKE_BROWSER";

    public static string? ConfiguredUrl => Environment.GetEnvironmentVariable(UrlVariable) is { Length: > 0 } url ? url.TrimEnd('/') : null;

    private IPlaywright? playwright;
    private IBrowser? browser;

    public string BaseUrl => ConfiguredUrl ?? throw new InvalidOperationException($"{UrlVariable} is not set.");

    public IBrowser Browser => browser ?? throw new InvalidOperationException("Browser not started.");

    public IAPIRequestContext Api { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        if (ConfiguredUrl is null)
        {
            return;
        }

        playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        var executable = Environment.GetEnvironmentVariable(BrowserVariable);
        browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            ExecutablePath = executable is { Length: > 0 } ? executable : null,
            Channel = executable is { Length: > 0 } ? null : "chrome",
        });
        Api = await playwright.APIRequest.NewContextAsync(new APIRequestNewContextOptions { BaseURL = BaseUrl });
    }

    public async Task DisposeAsync()
    {
        if (Api is not null)
        {
            await Api.DisposeAsync();
        }

        if (browser is not null)
        {
            await browser.CloseAsync();
        }

        playwright?.Dispose();
    }

    /// <summary>A new browser context of the given width with the localhost-only guard installed.</summary>
    public async Task<SmokeSession> SessionAsync(int width = 1280, int height = 800)
    {
        var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = BaseUrl,
            ViewportSize = new ViewportSize { Width = width, Height = height },
            AcceptDownloads = true,
        });
        var guard = await NetworkGuard.InstallAsync(context);
        return new SmokeSession(context, await context.NewPageAsync(), guard);
    }
}

[CollectionDefinition(Name)]
public sealed class SmokeCollection : ICollectionFixture<SmokeFixture>
{
    public const string Name = "Smoke";
}

/// <summary>One browser context and page. Disposing fails the test if any request left localhost.</summary>
public sealed class SmokeSession(IBrowserContext context, IPage page, NetworkGuard guard) : IAsyncDisposable
{
    public IBrowserContext Context => context;

    public IPage Page => page;

    public async ValueTask DisposeAsync()
    {
        await context.CloseAsync();
        Assert.True(guard.Blocked.IsEmpty, "Requests outside localhost: " + string.Join(", ", guard.Blocked));
    }
}

/// <summary>[Fact] skipped unless <see cref="SmokeFixture.UrlVariable"/> is set.</summary>
public sealed class SmokeFactAttribute : FactAttribute
{
    public SmokeFactAttribute()
    {
        if (SmokeFixture.ConfiguredUrl is null)
        {
            Skip = $"{SmokeFixture.UrlVariable} is not set.";
        }
    }
}

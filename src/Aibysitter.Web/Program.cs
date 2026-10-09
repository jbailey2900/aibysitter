using Aibysitter.Web.Linting;
using Aibysitter.Rules;
using Aibysitter.Web.Data;
using Aibysitter.Web.Gallery;
using Aibysitter.Web.GitHub;
using Aibysitter.Web.Hooks;
using Aibysitter.Web.Infrastructure;
using Aibysitter.Web.RulesPacks;
using Aibysitter.Web.Seo;
using Aibysitter.Web.Stats;
using Serilog;

RegexTimeout.Apply();

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console());

    builder.Services.AddRazorPages();
    builder.Services.AddSingleton(SiteOptions.From(builder.Configuration));
    builder.Services.AddHealthChecks();
    builder.Services.AddSingleton(_ => new LintEngine());
    builder.Services.AddSingleton<Aibysitter.Web.Linting.LintService>();
    builder.Services.AddSingleton<RuleFixHints>();
    builder.Services.AddHttpClient<RawGitHubFetcher>(client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("aibysitter (+https://aibysitting.net)");
        })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = System.Net.DecompressionMethods.All })
        .RemoveAllLoggers();
    builder.Services.AddSingleton<Aibysitter.Web.Samples.LintDemo>();
    builder.Services.AddSingleton<Aibysitter.Web.Gallery.GalleryCatalog>();
    builder.Services.AddSingleton<Aibysitter.Web.Notes.NoteCatalog>();
    builder.Services.AddSingleton<Aibysitter.Web.Incidents.IncidentCatalog>();
    builder.Services.AddSingleton<Aibysitter.Web.Seo.SiteMap>();
    builder.Services.AddSingleton(_ => new Aibysitter.Packs.PackCatalog());
    builder.Services.AddSingleton<Aibysitter.Web.RulesPacks.PackScores>();
    builder.Services.AddHostedService<CatalogStartupCheck>();
    builder.Services.AddAibysitterHardening(builder.Configuration);
    builder.Services.AddAibysitterDataProtection(builder.Configuration);
    builder.Services.AddAibysitterGitHubApp(builder.Configuration);
    builder.Services.AddAibysitterScoreHistory(builder.Configuration);
    builder.Services.AddAibysitterUsageStats(builder.Configuration);
    builder.Services.AddSingleton<BadgeService>();

    var app = builder.Build();

    app.UseForwardedHeaders();

    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Error");
        app.UseHsts();
    }

    app.UseSecurityHeaders();
    app.UseWhen(
        context => HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method),
        branch => branch.UseStatusCodePagesWithReExecute("/StatusCode/{0}"));
    app.UseSerilogRequestLogging();
    app.UseHttpsRedirection();
    app.UseRateLimiter();
    app.UseRouting();
    app.UseAuthorization();

    app.MapStaticAssets();
    app.MapRazorPages().WithStaticAssets();
    app.MapHealthChecks("/health");
    app.MapGitHubWebhook();
    app.MapLintApi();
    app.MapRegistry();
    app.MapGalleryDownloads();
    app.MapSeo();
    app.MapBadges();
    app.MapPackRegistry();
    app.MapHookFiles();

    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
    Environment.ExitCode = 1;
}
finally
{
    Log.CloseAndFlush();
}

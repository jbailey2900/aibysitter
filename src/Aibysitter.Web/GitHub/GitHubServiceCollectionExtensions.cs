using Aibysitter.Rules.PullRequests;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Aibysitter.Web.GitHub;

public static class GitHubServiceCollectionExtensions
{
    public static IServiceCollection AddAibysitterGitHubApp(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<GitHubOptions>(configuration.GetSection(GitHubOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IGitHubGateway, OctokitGitHubGateway>();
        services.Configure<ReviewQueueOptions>(configuration.GetSection(ReviewQueueOptions.SectionName));
        services.AddSingleton<IReviewJobStore>(CreateJobStore);
        services.AddSingleton<ReviewQueue>();
        services.AddSingleton(configuration.GetSection(Infrastructure.WebhookRateLimitSettings.SectionName).Get<Infrastructure.WebhookRateLimitSettings>()
            ?? new Infrastructure.WebhookRateLimitSettings());
        services.AddSingleton<WebhookSignatureLimiter>();
        services.AddSingleton(_ => new PullRequestReviewer());
        services.AddSingleton<ReviewProcessor>();
        services.AddHostedService<ReviewQueueRecovery>();
        services.AddHostedService<ReviewWorker>();
        return services;
    }

    private static IReviewJobStore CreateJobStore(IServiceProvider services)
    {
        var path = services.GetRequiredService<IOptions<ReviewQueueOptions>>().Value.Path;
        return string.IsNullOrWhiteSpace(path)
            ? new InMemoryReviewJobStore()
            : new FileReviewJobStore(path, services.GetRequiredService<TimeProvider>(), services.GetRequiredService<ILogger<FileReviewJobStore>>());
    }
}

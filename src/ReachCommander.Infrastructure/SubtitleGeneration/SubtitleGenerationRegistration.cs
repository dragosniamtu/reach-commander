using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ReachCommander.Application.SubtitleGeneration;

namespace ReachCommander.Infrastructure.SubtitleGeneration;

internal static class SubtitleGenerationRegistration
{
    internal static IServiceCollection AddSubtitleGeneration(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SubtitleGenerationOptions>()
            .Bind(configuration.GetSection(SubtitleGenerationOptions.SectionName))
            .Validate(options => !options.Enabled ||
                (Path.IsPathFullyQualified(options.SharedPath) && options.MaximumQueuedJobs is >= 1 and <= 100 &&
                 options.MaximumFiles is >= 1 and <= 10000 && options.MaximumDirectories is >= 1 and <= 100000 &&
                 options.MinimumFreeBytes >= 0 && options.WorkerTimeout > TimeSpan.Zero &&
                 options.WorkerTimeout <= TimeSpan.FromDays(1) && options.PollInterval >= TimeSpan.FromMilliseconds(10) &&
                 options.PollInterval <= TimeSpan.FromSeconds(5) && options.WorkerUnavailableGrace >= TimeSpan.FromSeconds(1) &&
                 options.WorkerUnavailableGrace <= TimeSpan.FromMinutes(1)),
                "Subtitle generation requires an absolute shared directory and bounded positive limits.")
            .ValidateOnStart();
        services.AddSingleton<SubtitleDiscovery>();
        services.AddSingleton<SubtitleWorkerReadiness>();
        services.AddSingleton<SubtitleJobStore>();
        services.AddSingleton<ISubtitleEngine, SubtitleMailboxEngine>();
        services.AddSingleton<ISubtitleInstaller, SubtitleInstaller>();
        services.AddSingleton<SubtitleGenerationService>();
        services.AddSingleton<ISubtitleGenerationService>(provider => provider.GetRequiredService<SubtitleGenerationService>());
        services.AddHostedService(provider => provider.GetRequiredService<SubtitleGenerationService>());
        return services;
    }
}

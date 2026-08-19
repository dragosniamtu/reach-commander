using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ReachCommander.Application.Sources;
using ReachCommander.Application.Files;
using ReachCommander.Infrastructure.Configuration;
using ReachCommander.Infrastructure.FileSystem;
using ReachCommander.Infrastructure.Security;

namespace ReachCommander.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddReachCommanderInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<ReachCommanderOptions>()
            .Bind(configuration.GetSection(ReachCommanderOptions.SectionName));
        services.AddSingleton<ISourceCatalog, JsonSourceCatalog>();
        services.AddSingleton<IPathSecurityService, PathSecurityService>();
        services.AddSingleton<IFileBrowser, LocalFileBrowser>();
        return services;
    }
}

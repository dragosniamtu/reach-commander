using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ReachCommander.Application.Sources;
using ReachCommander.Infrastructure.Configuration;

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
        return services;
    }
}

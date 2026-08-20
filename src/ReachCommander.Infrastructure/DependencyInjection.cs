using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ReachCommander.Application.Sources;
using ReachCommander.Application.Files;
using ReachCommander.Application.SystemMetrics;
using ReachCommander.Application.Uploads;
using ReachCommander.Infrastructure.Configuration;
using ReachCommander.Infrastructure.FileSystem;
using ReachCommander.Infrastructure.Security;
using ReachCommander.Infrastructure.SystemMetrics;
using ReachCommander.Infrastructure.SystemMetrics.Gpu;
using ReachCommander.Infrastructure.SystemMetrics.Linux;
using ReachCommander.Infrastructure.SystemMetrics.Windows;
using ReachCommander.Infrastructure.Mutations;
using ReachCommander.Infrastructure.Uploads;

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
        services
            .AddOptions<UploadOptions>()
            .Bind(configuration.GetSection(UploadOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<UploadOptions>, UploadOptionsValidator>();
        services.AddSingleton<UploadFilenameValidator>();
        services.AddSingleton<DirectoryMutationLock>();
        services.AddSingleton<IUploadFileSystem, LocalUploadFileSystem>();
        services.AddSingleton<IUploadService, UploadService>();
        services
            .AddOptions<HardwareMetricsOptions>()
            .Bind(configuration.GetSection(HardwareMetricsOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<HardwareMetricsOptions>, HardwareMetricsOptionsValidator>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IHostPlatform, RuntimeHostPlatform>();
        services.AddSingleton<BoundedTextFileReader>();
        services.AddSingleton<ITrustedPathResolver, TrustedPathResolver>();

        if (OperatingSystem.IsLinux())
        {
            services.AddSingleton<IHardwareMetricsCollector, LinuxProcCollector>();
            services.AddSingleton<IHardwareMetricsCollector, LinuxHwmonCollector>();
            services.AddSingleton<INativeLibraryLoader, RuntimeNativeLibraryLoader>();
            services.AddSingleton<INvidiaNvmlApi, NativeNvidiaNvmlApi>();
            services.AddSingleton<IHardwareMetricsCollector, NvidiaNvmlCollector>();
            services.AddSingleton<IHardwareMetricsCollector, LinuxDrmGpuCollector>();
        }

        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<ILibreHardwareSession, LibreHardwareSession>();
            services.AddSingleton<IWindowsSensorSource, LibreHardwareMonitorAdapter>();
            services.AddSingleton<IHardwareMetricsCollector, WindowsHardwareCollector>();
        }

        services.AddSingleton<IHardwareMetricsCollector, SourceStorageCollector>();
        services.AddSingleton<IHardwareCollectorRunner, BoundedHardwareCollectorRunner>();
        services.AddSingleton<IHardwareMetricsDelay, HardwareMetricsDelay>();
        services.AddSingleton<HardwareMetricsSnapshotCache>();
        services.AddSingleton<IHardwareMetricsSnapshotProvider>(provider =>
            provider.GetRequiredService<HardwareMetricsSnapshotCache>());
        services.AddHostedService<HardwareMetricsSampler>();
        return services;
    }
}

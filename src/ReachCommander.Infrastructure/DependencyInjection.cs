using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ReachCommander.Application.Authentication;
using ReachCommander.Application.Sources;
using ReachCommander.Application.Files;
using ReachCommander.Application.SystemMetrics;
using ReachCommander.Application.Uploads;
using ReachCommander.Application.BatchRenames;
using ReachCommander.Infrastructure.BatchRenames;
using ReachCommander.Infrastructure.Configuration;
using ReachCommander.Infrastructure.FileSystem;
using ReachCommander.Infrastructure.Security;
using ReachCommander.Infrastructure.SystemMetrics;
using ReachCommander.Infrastructure.SystemMetrics.Gpu;
using ReachCommander.Infrastructure.SystemMetrics.Linux;
using ReachCommander.Infrastructure.SystemMetrics.Windows;
using ReachCommander.Infrastructure.Mutations;
using ReachCommander.Infrastructure.Uploads;
using ReachCommander.Application.Archives;
using ReachCommander.Application.Directories;
using ReachCommander.Application.FileOperations;
using ReachCommander.Application.Trash;
using ReachCommander.Infrastructure.Archives;
using ReachCommander.Infrastructure.Archives.Catalog;
using ReachCommander.Infrastructure.Archives.Volumes;
using ReachCommander.Infrastructure.Archives.Worker;
using ReachCommander.Infrastructure.Archives.Extraction;
using ReachCommander.Infrastructure.Authentication;
using ReachCommander.Infrastructure.Directories;
using ReachCommander.Infrastructure.FileOperations;
using ReachCommander.Infrastructure.FileOperations.Execution;
using ReachCommander.Infrastructure.FileOperations.Persistence;
using ReachCommander.Infrastructure.FileOperations.Planning;
using ReachCommander.Infrastructure.Trash;
using ReachCommander.Application.SystemUpdates;
using ReachCommander.Infrastructure.SystemUpdates;

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
        services.AddSingleton(_ => AuthenticationDataPaths.Resolve(
            configuration["Authentication:DataPath"],
            OperatingSystem.IsWindows(),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
        services.AddSingleton<FileAuthenticationRepository>();
        services.AddSingleton<IPasswordHasher<AdministratorAccountDocument>,
            PasswordHasher<AdministratorAccountDocument>>();
        services.AddSingleton<AdministratorAccountService>();
        services.AddSingleton<IAdministratorAccountService>(provider =>
            provider.GetRequiredService<AdministratorAccountService>());
        services.AddHostedService<AuthenticationBootstrapHostedService>();
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
        services.AddSingleton<IBatchRenameFileSystem, LocalBatchRenameFileSystem>();
        services.AddSingleton<RenameRuleEvaluator>();
        services.AddSingleton<RenameNameValidator>();
        services.AddSingleton<BatchRenamePlanStore>();
        services.AddSingleton<BatchRenamePlanner>();
        services.AddSingleton<BatchRenameRequestLock>();
        services.AddSingleton<BatchRenameExecutor>();
        services.AddSingleton<IBatchRenameService, BatchRenameService>();
        services.AddSingleton(provider => FileOperationDataPaths.FromAuthenticationRoot(
            provider.GetRequiredService<AuthenticationDataPaths>().RootPath));
        services.AddSingleton<IFileOperationInspector, LocalFileOperationInspector>();
        services.AddSingleton<IFileOperationPlanStore, JsonFileOperationPlanStore>();
        services.AddSingleton<FileOperationPlanner>();
        services.AddSingleton<FileOperationRepository>();
        services.AddSingleton<FileOperationQueue>();
        services.AddSingleton<LocalFileOperationFileSystem>();
        services.AddSingleton<IFileOperationFileSystem>(provider =>
            provider.GetRequiredService<LocalFileOperationFileSystem>());
        services.AddSingleton<FileOperationExecutor>();
        services.AddSingleton(provider => new InterruptedOperationCleaner(
            provider.GetRequiredService<IPathSecurityService>(),
            provider.GetRequiredService<IFileOperationFileSystem>(),
            provider.GetRequiredService<FileOperationRepository>()));
        services.AddSingleton<IFileOperationService, FileOperationService>();
        services.AddSingleton<TrashManifestStore>();
        services.AddSingleton<TrashOperationExecutor>();
        services.AddSingleton<ITrashOperationExecutor>(provider =>
            provider.GetRequiredService<TrashOperationExecutor>());
        services.AddSingleton<ITrashService, TrashService>();
        services.AddSingleton<IDirectoryMutationService, DirectoryMutationService>();
        services.AddSingleton<FileOperationJobDispatcher>();
        services.AddSingleton<FileOperationWorker>();
        services.AddHostedService(provider =>
            provider.GetRequiredService<FileOperationWorker>());
        services
            .AddOptions<HardwareMetricsOptions>()
            .Bind(configuration.GetSection(HardwareMetricsOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<HardwareMetricsOptions>, HardwareMetricsOptionsValidator>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IHostPlatform, RuntimeHostPlatform>();
        services.AddSingleton<BoundedTextFileReader>();
        services.AddSingleton<ITrustedPathResolver, TrustedPathResolver>();

        services
            .AddOptions<ArchiveOptions>()
            .Bind(configuration.GetSection(ArchiveOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ArchiveOptions>, ArchiveOptionsValidator>();
        var archiveOptions = configuration
            .GetSection(ArchiveOptions.SectionName)
            .Get<ArchiveOptions>() ?? new ArchiveOptions();
        if (archiveOptions.Enabled)
        {
            services.AddSingleton<IArchivePartResolver, ArchivePartResolver>();
            services.AddSingleton<ArchiveCatalogBuilder>();
            services.AddSingleton<ArchiveCatalogCache>();
            services.AddSingleton<IArchiveWorkerProcessFactory, ArchiveWorkerProcessFactory>();
            services.AddSingleton<IArchiveWorkerDelay, ArchiveWorkerDelay>();
            services.AddSingleton<IArchiveWorkerClient, ArchiveWorkerClient>();
            services.AddSingleton<IArchiveCatalogProvider, ArchiveCatalogProvider>();
            services.AddSingleton<IArchiveBrowser, ArchiveBrowser>();
            services.AddSingleton<ArchiveExtractionPlanStore>();
            services.AddSingleton<ArchiveExtractionOperationStore>();
            services.AddSingleton<LocalArchiveExtractionRuntimeFileSystem>();
            services.AddSingleton<IArchiveExtractionFileSystem>(provider =>
                provider.GetRequiredService<LocalArchiveExtractionRuntimeFileSystem>());
            services.AddSingleton<IArchiveExtractionRuntimeFileSystem>(provider =>
                provider.GetRequiredService<LocalArchiveExtractionRuntimeFileSystem>());
            services.AddSingleton<IArchivePlanIdGenerator, ArchivePlanIdGenerator>();
            services.AddSingleton<IArchiveOperationIdGenerator, ArchiveOperationIdGenerator>();
            services.AddSingleton<ArchiveExtractionPlanner>();
            services.AddSingleton<ArchiveExtractionCoordinator>();
            services.AddSingleton<IArchiveExtractionService, ArchiveExtractionService>();
        }
        else
        {
            services.AddSingleton<IArchiveBrowser, DisabledArchiveBrowser>();
        }

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

        services
            .AddOptions<SystemUpdateOptions>()
            .Bind(configuration.GetSection(SystemUpdateOptions.SectionName));
        services.AddSingleton<ISystemUpdateRequestIdGenerator, SystemUpdateRequestIdGenerator>();
        services.AddSingleton<ISystemUpdateDelay, SystemUpdateDelay>();
        services.AddSingleton<ISystemUpdateMonitorDelay, SystemUpdateMonitorDelay>();
        services.AddSingleton<ISystemUpdateOperationMonitor, SystemUpdateOperationMonitor>();
        services.AddSingleton<ISystemMutationGate, SystemMutationGate>();
        services.AddSingleton<ISystemUpdateOperationProbe, SystemUpdateOperationProbe>();
        var systemUpdateOptions = configuration
            .GetSection(SystemUpdateOptions.SectionName)
            .Get<SystemUpdateOptions>() ?? new SystemUpdateOptions();
        if (OperatingSystem.IsLinux() &&
            systemUpdateOptions.Enabled &&
            File.Exists(systemUpdateOptions.SocketPath))
        {
            services.AddSingleton<ISystemUpdaterTransport, UnixSystemUpdaterTransport>();
            services.AddSingleton<ISystemUpdaterGateway, SystemUpdaterGateway>();
            services.AddSingleton<ISystemUpdateDiagnosticsGateway, SystemUpdateDiagnosticsGateway>();
        }
        else
        {
            services.AddSingleton<ISystemUpdaterGateway, UnavailableSystemUpdaterGateway>();
            services.AddSingleton<ISystemUpdateDiagnosticsGateway,
                UnavailableSystemUpdateDiagnosticsGateway>();
        }

        services.AddSingleton<ISystemUpdateSupportBundleService, SystemUpdateSupportBundleService>();
        services.AddSingleton<SystemUpdateCoordinator>();
        services.AddSingleton<ISystemUpdateService>(provider =>
            provider.GetRequiredService<SystemUpdateCoordinator>());
        services.AddHostedService(provider =>
            provider.GetRequiredService<SystemUpdateCoordinator>());
        return services;
    }
}

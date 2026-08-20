namespace ReachCommander.Infrastructure.Archives;

public sealed class ArchiveOptions
{
    public const string SectionName = "Archives";

    public bool Enabled { get; init; } = true;

    public int MaxEntries { get; init; } = 100_000;

    public int MaxVolumes { get; init; } = 100;

    public long MaxTotalCompressedBytes { get; init; } = 500L * 1024 * 1024 * 1024;

    public long MaxTotalExtractedBytes { get; init; } = 500L * 1024 * 1024 * 1024;

    public long MaxSingleExtractedFileBytes { get; init; } = 200L * 1024 * 1024 * 1024;

    public int MaxExpansionRatio { get; init; } = 1_000;

    public int MaxPathDepth { get; init; } = 64;

    public int MaxPathCharacters { get; init; } = 4_096;

    public int MaxComponentCharacters { get; init; } = 255;

    public int MaxConcurrentExtractions { get; init; } = 1;

    public TimeSpan InspectionTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan ExtractionTimeout { get; init; } = TimeSpan.FromHours(6);

    public long WorkerManagedMemoryBytes { get; init; } = 1L * 1024 * 1024 * 1024;

    public long WorkerWorkingSetBytes { get; init; } = 1_536L * 1024 * 1024;

    public TimeSpan PlanLifetime { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan CatalogLifetime { get; init; } = TimeSpan.FromMinutes(5);

    public int MaxCachedCatalogs { get; init; } = 16;

    public int MaxCachedEntries { get; init; } = 250_000;
}

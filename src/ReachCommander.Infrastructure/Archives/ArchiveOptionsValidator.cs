using Microsoft.Extensions.Options;

namespace ReachCommander.Infrastructure.Archives;

internal sealed class ArchiveOptionsValidator : IValidateOptions<ArchiveOptions>
{
    private const int MaximumCachedCatalogs = 1_024;

    public ValidateOptionsResult Validate(string? name, ArchiveOptions options)
    {
        if (!AllPositive(options))
        {
            return ValidateOptionsResult.Fail("All archive limits must be positive.");
        }

        if (options.MaxSingleExtractedFileBytes > options.MaxTotalExtractedBytes)
        {
            return ValidateOptionsResult.Fail(
                "MaxSingleExtractedFileBytes cannot exceed MaxTotalExtractedBytes.");
        }

        if (options.WorkerWorkingSetBytes < options.WorkerManagedMemoryBytes)
        {
            return ValidateOptionsResult.Fail(
                "WorkerWorkingSetBytes cannot be lower than WorkerManagedMemoryBytes.");
        }

        if (options.MaxComponentCharacters > options.MaxPathCharacters ||
            options.MaxPathDepth > options.MaxPathCharacters)
        {
            return ValidateOptionsResult.Fail(
                "Archive path limits cannot represent the configured depth and components.");
        }

        if (options.MaxCachedCatalogs > MaximumCachedCatalogs ||
            options.MaxCachedEntries < options.MaxEntries)
        {
            return ValidateOptionsResult.Fail("Archive cache limits are inconsistent.");
        }

        try
        {
            _ = checked(options.MaxTotalCompressedBytes * options.MaxExpansionRatio);
        }
        catch (OverflowException)
        {
            return ValidateOptionsResult.Fail("Archive byte and ratio arithmetic exceeds Int64.");
        }

        return ValidateOptionsResult.Success;
    }

    private static bool AllPositive(ArchiveOptions options) =>
        options.MaxEntries > 0 &&
        options.MaxVolumes > 0 &&
        options.MaxTotalCompressedBytes > 0 &&
        options.MaxTotalExtractedBytes > 0 &&
        options.MaxSingleExtractedFileBytes > 0 &&
        options.MaxExpansionRatio > 0 &&
        options.MaxPathDepth > 0 &&
        options.MaxPathCharacters > 0 &&
        options.MaxComponentCharacters > 0 &&
        options.MaxConcurrentExtractions > 0 &&
        options.InspectionTimeout > TimeSpan.Zero &&
        options.ExtractionTimeout > TimeSpan.Zero &&
        options.WorkerManagedMemoryBytes > 0 &&
        options.WorkerWorkingSetBytes > 0 &&
        options.PlanLifetime > TimeSpan.Zero &&
        options.CatalogLifetime > TimeSpan.Zero &&
        options.MaxCachedCatalogs > 0 &&
        options.MaxCachedEntries > 0;
}

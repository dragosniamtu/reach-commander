using ReachCommander.Infrastructure.Archives;

namespace ReachCommander.UnitTests.Archives;

public sealed class ArchiveOptionsValidatorTests
{
    private readonly ArchiveOptionsValidator _validator = new();

    [Fact]
    public void Defaults_match_the_approved_safety_envelope()
    {
        var value = new ArchiveOptions();

        Assert.True(value.Enabled);
        Assert.Equal(100_000, value.MaxEntries);
        Assert.Equal(100, value.MaxVolumes);
        Assert.Equal(500L * 1024 * 1024 * 1024, value.MaxTotalCompressedBytes);
        Assert.Equal(500L * 1024 * 1024 * 1024, value.MaxTotalExtractedBytes);
        Assert.Equal(200L * 1024 * 1024 * 1024, value.MaxSingleExtractedFileBytes);
        Assert.Equal(1_000, value.MaxExpansionRatio);
        Assert.Equal(64, value.MaxPathDepth);
        Assert.Equal(4_096, value.MaxPathCharacters);
        Assert.Equal(255, value.MaxComponentCharacters);
        Assert.Equal(1, value.MaxConcurrentExtractions);
        Assert.Equal(TimeSpan.FromSeconds(30), value.InspectionTimeout);
        Assert.Equal(TimeSpan.FromHours(6), value.ExtractionTimeout);
        Assert.Equal(1L * 1024 * 1024 * 1024, value.WorkerManagedMemoryBytes);
        Assert.Equal(1_536L * 1024 * 1024, value.WorkerWorkingSetBytes);
        Assert.Equal(TimeSpan.FromMinutes(10), value.PlanLifetime);
        Assert.Equal(TimeSpan.FromMinutes(5), value.CatalogLifetime);
        Assert.Equal(16, value.MaxCachedCatalogs);
        Assert.Equal(250_000, value.MaxCachedEntries);
        Assert.True(_validator.Validate(null, value).Succeeded);
    }

    [Fact]
    public void Rejects_a_single_file_limit_above_the_total_limit()
    {
        var value = new ArchiveOptions
        {
            MaxSingleExtractedFileBytes = 11,
            MaxTotalExtractedBytes = 10,
        };

        Assert.True(_validator.Validate(null, value).Failed);
    }

    [Fact]
    public void Rejects_a_working_set_below_the_managed_heap_limit()
    {
        var value = new ArchiveOptions
        {
            WorkerManagedMemoryBytes = 2_048,
            WorkerWorkingSetBytes = 1_024,
        };

        Assert.True(_validator.Validate(null, value).Failed);
    }

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public void Rejects_invalid_positive_and_consistency_constraints(ArchiveOptions value)
        => Assert.True(_validator.Validate(null, value).Failed);

    public static TheoryData<ArchiveOptions> InvalidOptions => new()
    {
        new ArchiveOptions { MaxEntries = 0 },
        new ArchiveOptions { MaxVolumes = 0 },
        new ArchiveOptions { MaxTotalCompressedBytes = 0 },
        new ArchiveOptions { MaxTotalExtractedBytes = 0 },
        new ArchiveOptions { MaxSingleExtractedFileBytes = 0 },
        new ArchiveOptions { MaxExpansionRatio = 0 },
        new ArchiveOptions { MaxPathDepth = 0 },
        new ArchiveOptions { MaxPathCharacters = 0 },
        new ArchiveOptions { MaxComponentCharacters = 0 },
        new ArchiveOptions { MaxConcurrentExtractions = 0 },
        new ArchiveOptions { InspectionTimeout = TimeSpan.Zero },
        new ArchiveOptions { ExtractionTimeout = TimeSpan.Zero },
        new ArchiveOptions { WorkerManagedMemoryBytes = 0 },
        new ArchiveOptions { WorkerWorkingSetBytes = 0 },
        new ArchiveOptions { PlanLifetime = TimeSpan.Zero },
        new ArchiveOptions { CatalogLifetime = TimeSpan.Zero },
        new ArchiveOptions { MaxCachedCatalogs = 0 },
        new ArchiveOptions { MaxCachedEntries = 0 },
        new ArchiveOptions { MaxPathCharacters = 200, MaxComponentCharacters = 201 },
        new ArchiveOptions { MaxCachedCatalogs = 1_025 },
    };
}

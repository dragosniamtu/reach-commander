using Microsoft.Extensions.Options;
using ReachCommander.Infrastructure.Uploads;

namespace ReachCommander.UnitTests.Uploads;

public sealed class UploadOptionsValidatorTests
{
    private readonly UploadOptionsValidator _validator = new();

    [Fact]
    public void Defaults_match_the_approved_limits()
    {
        var options = new UploadOptions();

        Assert.Equal(10L * 1024 * 1024 * 1024, options.MaxFileBytes);
        Assert.Equal(50L * 1024 * 1024 * 1024, options.MaxBatchBytes);
        Assert.Equal(100, options.MaxFilesPerBatch);
        Assert.Equal(2, options.MaxConcurrentBatches);
        Assert.True(_validator.Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData(0, 1, 1, 1)]
    [InlineData(2, 1, 1, 1)]
    [InlineData(1, 1, 0, 1)]
    [InlineData(1, 1, 1, 0)]
    public void Invalid_or_inconsistent_limits_fail_startup(
        long maxFileBytes,
        long maxBatchBytes,
        int maxFiles,
        int maxConcurrent)
    {
        var options = new UploadOptions
        {
            MaxFileBytes = maxFileBytes,
            MaxBatchBytes = maxBatchBytes,
            MaxFilesPerBatch = maxFiles,
            MaxConcurrentBatches = maxConcurrent,
        };

        Assert.True(_validator.Validate(null, options).Failed);
    }

    [Fact]
    public void Excessive_supported_ranges_fail_startup()
    {
        Assert.True(_validator.Validate(null, new UploadOptions
        {
            MaxFileBytes = 1,
            MaxBatchBytes = 1,
            MaxFilesPerBatch = 10_001,
            MaxConcurrentBatches = 1,
        }).Failed);

        Assert.True(_validator.Validate(null, new UploadOptions
        {
            MaxFileBytes = 1,
            MaxBatchBytes = 1,
            MaxFilesPerBatch = 1,
            MaxConcurrentBatches = 65,
        }).Failed);
    }

    [Fact]
    public void Request_ceiling_overflow_fails_startup()
    {
        var options = new UploadOptions
        {
            MaxFileBytes = long.MaxValue,
            MaxBatchBytes = long.MaxValue,
            MaxFilesPerBatch = 1,
            MaxConcurrentBatches = 1,
        };

        Assert.True(_validator.Validate(null, options).Failed);
    }
}

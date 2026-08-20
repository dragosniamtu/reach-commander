using Microsoft.Extensions.Options;

namespace ReachCommander.Infrastructure.Uploads;

public sealed class UploadOptions
{
    public const string SectionName = "Uploads";

    public long MaxFileBytes { get; init; } = 10L * 1024 * 1024 * 1024;

    public long MaxBatchBytes { get; init; } = 50L * 1024 * 1024 * 1024;

    public int MaxFilesPerBatch { get; init; } = 100;

    public int MaxConcurrentBatches { get; init; } = 2;
}

internal sealed class UploadOptionsValidator : IValidateOptions<UploadOptions>
{
    private const int MaximumFilesPerBatch = 10_000;
    private const int MaximumConcurrentBatches = 64;

    public ValidateOptionsResult Validate(string? name, UploadOptions options)
    {
        if (options.MaxFileBytes <= 0 ||
            options.MaxBatchBytes <= 0 ||
            options.MaxFilesPerBatch <= 0 ||
            options.MaxConcurrentBatches <= 0)
        {
            return ValidateOptionsResult.Fail("All upload limits must be positive.");
        }

        if (options.MaxFileBytes > options.MaxBatchBytes)
        {
            return ValidateOptionsResult.Fail("MaxFileBytes cannot exceed MaxBatchBytes.");
        }

        if (options.MaxFilesPerBatch > MaximumFilesPerBatch ||
            options.MaxConcurrentBatches > MaximumConcurrentBatches)
        {
            return ValidateOptionsResult.Fail("Upload count or concurrency exceeds supported limits.");
        }

        try
        {
            _ = UploadRequestLimit.Calculate(options);
        }
        catch (OverflowException)
        {
            return ValidateOptionsResult.Fail("The configured multipart request ceiling exceeds Int64.");
        }

        return ValidateOptionsResult.Success;
    }
}

internal static class UploadRequestLimit
{
    public static long Calculate(UploadOptions options)
    {
        var overhead = Math.Min(
            1L * 1024 * 1024 * 1024,
            checked(1L * 1024 * 1024 + (long)options.MaxFilesPerBatch * 16 * 1024));
        return checked(options.MaxBatchBytes + overhead);
    }
}

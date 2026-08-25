using ReachCommander.Application.FileOperations;

namespace ReachCommander.Infrastructure.FileOperations.Execution;

internal sealed class FileOperationProgressTracker(
    TimeProvider clock,
    int totalItems,
    long? totalBytes)
{
    private readonly DateTimeOffset _startedAt = clock.GetUtcNow();
    private int _completedItems;
    private long _completedBytes;

    internal long CompletedBytes => _completedBytes;

    internal FileOperationProgress Report(
        string? currentLogicalName,
        int completedItems,
        long completedBytes)
    {
        if (completedItems < _completedItems ||
            completedBytes < _completedBytes ||
            completedItems > totalItems ||
            completedBytes < 0)
        {
            throw new InvalidOperationException("File operation progress must be monotonic.");
        }

        _completedItems = completedItems;
        _completedBytes = completedBytes;
        var elapsed = clock.GetUtcNow() - _startedAt;
        double? percentage = totalBytes switch
        {
            null => null,
            0 => completedItems >= totalItems ? 100d : 0d,
            _ => Math.Clamp(completedBytes * 100d / totalBytes.Value, 0, 100),
        };
        long? rate = elapsed.TotalSeconds > 0 && completedBytes > 0
            ? (long)Math.Floor(completedBytes / elapsed.TotalSeconds)
            : null;
        TimeSpan? remaining = totalBytes is not null && rate is > 0
            ? TimeSpan.FromSeconds(Math.Max(0, totalBytes.Value - completedBytes) / (double)rate.Value)
            : null;
        return new FileOperationProgress(
            currentLogicalName,
            completedItems,
            totalItems,
            completedBytes,
            totalBytes,
            percentage,
            rate,
            elapsed,
            remaining);
    }
}

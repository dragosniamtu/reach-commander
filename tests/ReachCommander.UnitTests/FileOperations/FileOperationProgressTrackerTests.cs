using ReachCommander.Infrastructure.FileOperations.Execution;

namespace ReachCommander.UnitTests.FileOperations;

public sealed class FileOperationProgressTrackerTests
{
    [Fact]
    public void Report_calculates_monotonic_percentage_rate_and_eta()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-25T10:00:00Z"));
        var tracker = new FileOperationProgressTracker(clock, totalItems: 3, totalBytes: 100);
        clock.Advance(TimeSpan.FromSeconds(2));

        var progress = tracker.Report("movie.mkv", completedItems: 1, completedBytes: 40);

        Assert.Equal(40, progress.Percentage);
        Assert.Equal(20, progress.BytesPerSecond);
        Assert.Equal(TimeSpan.FromSeconds(3), progress.EstimatedRemaining);
        Assert.Throws<InvalidOperationException>(() =>
            tracker.Report("movie.mkv", completedItems: 0, completedBytes: 39));
    }

    [Fact]
    public void Report_keeps_unknown_totals_indeterminate()
    {
        var tracker = new FileOperationProgressTracker(
            TimeProvider.System,
            totalItems: 1,
            totalBytes: null);

        var progress = tracker.Report("stream.bin", 0, 20);

        Assert.Null(progress.Percentage);
        Assert.Null(progress.EstimatedRemaining);
    }

    private sealed class ManualTimeProvider(DateTimeOffset current) : TimeProvider
    {
        private DateTimeOffset _current = current;

        public override DateTimeOffset GetUtcNow() => _current;

        public void Advance(TimeSpan duration) => _current = _current.Add(duration);
    }
}

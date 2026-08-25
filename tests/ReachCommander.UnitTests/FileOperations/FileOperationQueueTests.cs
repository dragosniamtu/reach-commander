using ReachCommander.Infrastructure.FileOperations.Persistence;

namespace ReachCommander.UnitTests.FileOperations;

public sealed class FileOperationQueueTests
{
    [Fact]
    public async Task Signal_coalesces_concurrent_wakeups()
    {
        var queue = new FileOperationQueue();

        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        Parallel.For(0, 1_000, _ =>
        {
            try { queue.Signal(); }
            catch (Exception exception) { failures.Enqueue(exception); }
        });
        await queue.WaitAsync(default);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Empty(failures);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            queue.WaitAsync(cancellation.Token));
    }
}

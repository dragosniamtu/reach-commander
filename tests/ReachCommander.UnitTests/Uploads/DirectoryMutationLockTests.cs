using ReachCommander.Infrastructure.Mutations;

namespace ReachCommander.UnitTests.Uploads;

public sealed class DirectoryMutationLockTests
{
    [Fact]
    public async Task Same_logical_directory_is_serialized_case_insensitively_by_source()
    {
        var gate = new DirectoryMutationLock();
        await using var first = await gate.AcquireAsync("media", "/Movies", CancellationToken.None);

        var second = gate.AcquireAsync("MEDIA", "/Movies", CancellationToken.None).AsTask();

        Assert.False(second.IsCompleted);
        await first.DisposeAsync();
        await using var secondLease = await second.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Ancestor_and_descendant_directories_are_serialized()
    {
        var gate = new DirectoryMutationLock();
        await using var parent = await gate.AcquireAsync("media", "/", CancellationToken.None);

        var descendant = gate.AcquireAsync("media", "/Movies/Incoming", CancellationToken.None).AsTask();

        Assert.False(descendant.IsCompleted);
        await parent.DisposeAsync();
        await using var descendantLease = await descendant.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Sibling_directories_and_other_sources_can_proceed_concurrently()
    {
        var gate = new DirectoryMutationLock();
        await using var movies = await gate.AcquireAsync("media", "/Movies", CancellationToken.None);

        await using var music = await gate
            .AcquireAsync("media", "/Music", CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(1));
        await using var downloads = await gate
            .AcquireAsync("downloads", "/Movies", CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Segment_boundaries_do_not_create_false_ancestor_conflicts()
    {
        var gate = new DirectoryMutationLock();
        await using var movies = await gate.AcquireAsync("media", "/Movies", CancellationToken.None);

        await using var moviesOld = await gate
            .AcquireAsync("media", "/Movies-Old", CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Cancelled_waiter_never_enters_and_does_not_block_following_work()
    {
        var gate = new DirectoryMutationLock();
        await using var first = await gate.AcquireAsync("media", "/Movies", CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var waiting = gate.AcquireAsync("media", "/Movies", cancellation.Token).AsTask();

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await first.DisposeAsync();
        await using var following = await gate
            .AcquireAsync("media", "/Movies", CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Lease_releases_exactly_once()
    {
        var gate = new DirectoryMutationLock();
        var lease = await gate.AcquireAsync("media", "/Movies", CancellationToken.None);

        await lease.DisposeAsync();
        await lease.DisposeAsync();
        await using var first = await gate.AcquireAsync("media", "/Movies", CancellationToken.None);
        var second = gate.AcquireAsync("media", "/Movies", CancellationToken.None).AsTask();

        Assert.False(second.IsCompleted);
        await first.DisposeAsync();
        await using var secondLease = await second.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Promoted_waiter_lease_releases_the_directory()
    {
        var gate = new DirectoryMutationLock();
        var first = await gate.AcquireAsync("media", "/Movies", CancellationToken.None);
        var secondTask = gate.AcquireAsync("media", "/Movies", CancellationToken.None).AsTask();
        await first.DisposeAsync();
        var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(1));

        await second.DisposeAsync();

        await using var third = await gate
            .AcquireAsync("media", "/Movies", CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromMilliseconds(200));
    }
}

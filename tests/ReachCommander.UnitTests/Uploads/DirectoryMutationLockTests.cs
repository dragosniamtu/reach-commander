using ReachCommander.Infrastructure.Mutations;

namespace ReachCommander.UnitTests.Uploads;

public sealed class DirectoryMutationLockTests
{
    [Fact]
    public async Task Acquire_many_deduplicates_keys_and_releases_them_together()
    {
        var gate = new DirectoryMutationLock();
        var lease = await gate.AcquireManyAsync(
            [
                new DirectoryMutationTarget("media", "/Movies"),
                new DirectoryMutationTarget("MEDIA", "/Movies"),
                new DirectoryMutationTarget("downloads", "/Incoming"),
            ],
            CancellationToken.None);

        var media = gate.AcquireAsync("media", "/Movies", CancellationToken.None).AsTask();
        var downloads = gate.AcquireAsync("downloads", "/Incoming", CancellationToken.None).AsTask();
        Assert.False(media.IsCompleted);
        Assert.False(downloads.IsCompleted);

        await lease.DisposeAsync();
        await using var mediaLease = await media.WaitAsync(TimeSpan.FromSeconds(1));
        await using var downloadsLease = await downloads.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Opposite_input_orders_complete_without_deadlock()
    {
        var gate = new DirectoryMutationLock();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = AcquireAfterStartAsync(
            gate,
            start.Task,
            [new("media", "/A"), new("media", "/B")]);
        var second = AcquireAfterStartAsync(
            gate,
            start.Task,
            [new("media", "/B"), new("media", "/A")]);
        start.SetResult();

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Cancelled_multi_acquisition_releases_earlier_leases()
    {
        var gate = new DirectoryMutationLock();
        await using var blocker = await gate.AcquireAsync("media", "/B", CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var waiting = gate.AcquireManyAsync(
            [new("media", "/A"), new("media", "/B")],
            cancellation.Token).AsTask();

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await using var released = await gate
            .AcquireAsync("media", "/A", CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Acquire_many_collapses_descendants_covered_by_an_ancestor()
    {
        var sut = new DirectoryMutationLock();
        using var cancellation = new CancellationTokenSource();

        var pending = sut.AcquireManyAsync(
            [
                new("media", "/Albums/2026"),
                new("media", "/Albums"),
            ],
            cancellation.Token);
        if (!pending.IsCompletedSuccessfully)
        {
            cancellation.Cancel();
        }

        Assert.True(pending.IsCompletedSuccessfully);
        await using (await pending)
        {
            using var blockedCancellation = new CancellationTokenSource();
            var blocked = sut.AcquireAsync(
                "media",
                "/Albums/Other",
                blockedCancellation.Token);
            Assert.False(blocked.IsCompletedSuccessfully);
            blockedCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked.AsTask());
        }

        await using var released = await sut.AcquireAsync(
            "media",
            "/Albums/Other",
            CancellationToken.None);
    }

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

    private static async Task AcquireAfterStartAsync(
        DirectoryMutationLock gate,
        Task start,
        IReadOnlyList<DirectoryMutationTarget> targets)
    {
        await start;
        await using var lease = await gate.AcquireManyAsync(targets, CancellationToken.None);
        await Task.Yield();
    }
}

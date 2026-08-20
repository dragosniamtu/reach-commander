using ReachCommander.Application.BatchRenames;
using ReachCommander.Infrastructure.Mutations;
using ReachCommander.UnitTests.Support;

namespace ReachCommander.UnitTests.BatchRenames;

public sealed class BatchRenameExecutorTests : IDisposable
{
    private readonly BatchRenameTestFixture _fixture = new();

    [Fact]
    public async Task Execute_supports_a_two_way_swap()
    {
        _fixture.WriteFile("Movies/a.txt", "A");
        _fixture.WriteFile("Movies/b.txt", "B");
        var executor = _fixture.CreateExecutor();
        var plan = _fixture.StoredPlan("/Movies", ("a.txt", "b.txt"), ("b.txt", "a.txt"));

        var outcome = await executor.ExecuteAsync(Guid.NewGuid(), plan, CancellationToken.None);

        Assert.Equal(BatchRenameOperationStatus.Completed, outcome.Result.Status);
        Assert.Equal("B", _fixture.ReadFile("Movies/a.txt"));
        Assert.Equal("A", _fixture.ReadFile("Movies/b.txt"));
        Assert.Empty(_fixture.ReservedTemporaryEntries("Movies"));
    }

    [Fact]
    public async Task Execute_supports_a_three_entry_cycle_and_case_only_change()
    {
        _fixture.WriteFile("Movies/a.txt", "A");
        _fixture.WriteFile("Movies/b.txt", "B");
        _fixture.WriteFile("Movies/c.txt", "C");
        var executor = _fixture.CreateExecutor();

        var cycle = await executor.ExecuteAsync(Guid.NewGuid(), _fixture.StoredPlan(
            "/Movies", ("a.txt", "b.txt"), ("b.txt", "c.txt"), ("c.txt", "a.txt")), CancellationToken.None);
        var casing = await executor.ExecuteAsync(Guid.NewGuid(), _fixture.StoredPlan(
            "/Movies", ("a.txt", "A.TXT")), CancellationToken.None);

        Assert.Equal(BatchRenameOperationStatus.Completed, cycle.Result.Status);
        Assert.Equal(BatchRenameOperationStatus.Completed, casing.Result.Status);
        Assert.True(_fixture.EntryExists("Movies/A.TXT"));
        Assert.Empty(_fixture.ReservedTemporaryEntries("Movies"));
    }

    [Fact]
    public async Task Execute_compensates_every_completed_move_after_a_failure()
    {
        _fixture.WriteFile("Movies/a.txt", "A");
        _fixture.WriteFile("Movies/b.txt", "B");
        var failingFileSystem = _fixture.CreateFailingFileSystem(4);
        var executor = _fixture.CreateExecutor(failingFileSystem);

        var outcome = await executor.ExecuteAsync(Guid.NewGuid(), _fixture.StoredPlan(
            "/Movies", ("a.txt", "one.txt"), ("b.txt", "two.txt")), CancellationToken.None);

        Assert.Equal(BatchRenameOperationStatus.Failed, outcome.Result.Status);
        Assert.True(outcome.Result.CompensationAttempted);
        Assert.False(outcome.Result.RecoveryRequired);
        Assert.Equal("A", _fixture.ReadFile("Movies/a.txt"));
        Assert.Equal("B", _fixture.ReadFile("Movies/b.txt"));
        Assert.Empty(_fixture.ReservedTemporaryEntries("Movies"));
    }

    [Fact]
    public async Task Execute_reports_recovery_required_when_compensation_also_fails()
    {
        _fixture.WriteFile("Movies/a.txt", "A");
        _fixture.WriteFile("Movies/b.txt", "B");
        var failingFileSystem = _fixture.CreateFailingFileSystem(4, 5);

        var outcome = await _fixture.CreateExecutor(failingFileSystem).ExecuteAsync(
            Guid.NewGuid(),
            _fixture.StoredPlan("/Movies", ("a.txt", "one.txt"), ("b.txt", "two.txt")),
            CancellationToken.None);

        Assert.Equal(BatchRenameOperationStatus.RecoveryRequired, outcome.Result.Status);
        Assert.True(outcome.Result.RecoveryRequired);
        Assert.Contains(outcome.Result.Rows, row => row.Result == BatchRenameRowResult.RecoveryRequired);
        Assert.All(outcome.Result.Rows, row =>
            Assert.DoesNotContain(_fixture.SourceRoot, row.Message ?? string.Empty));
    }

    [Fact]
    public async Task Execute_waits_for_the_shared_directory_mutation_lock()
    {
        _fixture.WriteFile("Movies/a.txt", "A");
        var mutationLock = new DirectoryMutationLock();
        var heldLease = await mutationLock.AcquireAsync("MEDIA", "/Movies", CancellationToken.None);
        var executor = _fixture.CreateExecutor(mutationLock: mutationLock);

        var execution = executor.ExecuteAsync(
            Guid.NewGuid(),
            _fixture.StoredPlan("/Movies", ("a.txt", "renamed.txt")),
            CancellationToken.None).AsTask();
        await Task.Yield();

        Assert.False(execution.IsCompleted);
        await heldLease.DisposeAsync();
        var outcome = await execution;
        Assert.Equal(BatchRenameOperationStatus.Completed, outcome.Result.Status);
    }

    public void Dispose() => _fixture.Dispose();
}

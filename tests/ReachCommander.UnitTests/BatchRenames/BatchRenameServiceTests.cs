using ReachCommander.Application.BatchRenames;
using ReachCommander.UnitTests.Support;

namespace ReachCommander.UnitTests.BatchRenames;

public sealed class BatchRenameServiceTests : IDisposable
{
    private readonly BatchRenameTestFixture _fixture = new();

    [Fact]
    public async Task Execute_is_idempotent_for_concurrent_and_later_retries()
    {
        _fixture.WriteFile("Movies/a.txt", "A");
        var service = _fixture.CreateService();
        var preview = await service.PreviewAsync(
            _fixture.Command("/Movies", ["a.txt"], _fixture.Rules("renamed", "txt")),
            CancellationToken.None);

        var concurrent = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            service.ExecuteAsync(preview.PlanId, CancellationToken.None).AsTask()));
        var retry = await service.ExecuteAsync(preview.PlanId, CancellationToken.None);

        Assert.All(concurrent, result => Assert.Equal(concurrent[0], result));
        Assert.Equal(concurrent[0], retry);
        Assert.False(_fixture.EntryExists("Movies/a.txt"));
        Assert.Equal("A", _fixture.ReadFile("Movies/renamed.txt"));
        Assert.True(retry.UndoAvailable);
        Assert.NotNull(retry.UndoExpiresAt);
    }

    [Fact]
    public async Task Undo_restores_the_whole_batch_once_for_concurrent_and_later_retries()
    {
        _fixture.WriteFile("Movies/a.txt", "A");
        _fixture.CreateDirectory("Movies/Drafts");
        var service = _fixture.CreateService();
        var preview = await service.PreviewAsync(
            _fixture.Command(
                "/Movies",
                ["a.txt", "Drafts"],
                _fixture.Rules("Archive-[C]", "[E]")),
            CancellationToken.None);
        var operation = await service.ExecuteAsync(preview.PlanId, CancellationToken.None);

        var concurrent = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            service.UndoAsync(operation.OperationId, CancellationToken.None).AsTask()));
        var retry = await service.UndoAsync(operation.OperationId, CancellationToken.None);

        Assert.All(concurrent, result => Assert.Equal(concurrent[0], result));
        Assert.Equal(BatchRenameOperationStatus.Undone, concurrent[0].Status);
        Assert.Equal(concurrent[0], retry);
        Assert.False(retry.UndoAvailable);
        Assert.True(_fixture.EntryExists("Movies/a.txt"));
        Assert.True(_fixture.EntryExists("Movies/Drafts"));
    }

    [Fact]
    public async Task Undo_blocks_the_entire_batch_when_one_destination_changed()
    {
        _fixture.WriteFile("Movies/a.txt", "A");
        _fixture.WriteFile("Movies/b.txt", "B");
        var service = _fixture.CreateService();
        var preview = await service.PreviewAsync(
            _fixture.Command(
                "/Movies",
                ["a.txt", "b.txt"],
                _fixture.Rules("item-[C]", "txt")),
            CancellationToken.None);
        var operation = await service.ExecuteAsync(preview.PlanId, CancellationToken.None);
        _fixture.WriteFile("Movies/item-1.txt", "changed content");

        await Assert.ThrowsAsync<RenamePlanStaleException>(() =>
            service.UndoAsync(operation.OperationId, CancellationToken.None).AsTask());

        Assert.True(_fixture.EntryExists("Movies/item-1.txt"));
        Assert.True(_fixture.EntryExists("Movies/item-2.txt"));
        Assert.False(_fixture.EntryExists("Movies/a.txt"));
        Assert.False(_fixture.EntryExists("Movies/b.txt"));
    }

    [Fact]
    public async Task Execute_rejects_expired_or_non_executable_plans_without_mutation()
    {
        _fixture.WriteFile("Movies/a.txt", "A");
        var service = _fixture.CreateService();
        var invalid = await service.PreviewAsync(
            _fixture.Command("/Movies", ["a.txt"], _fixture.Rules("a", "txt")),
            CancellationToken.None);
        await Assert.ThrowsAsync<InvalidRenameRuleException>(() =>
            service.ExecuteAsync(invalid.PlanId, CancellationToken.None).AsTask());

        var valid = await service.PreviewAsync(
            _fixture.Command("/Movies", ["a.txt"], _fixture.Rules("renamed", "txt")),
            CancellationToken.None);
        _fixture.Clock.Advance(TimeSpan.FromMinutes(11));
        await Assert.ThrowsAsync<RenamePlanExpiredException>(() =>
            service.ExecuteAsync(valid.PlanId, CancellationToken.None).AsTask());
        Assert.True(_fixture.EntryExists("Movies/a.txt"));
    }

    [Fact]
    public async Task Undo_expires_without_mutating_the_completed_rename()
    {
        _fixture.WriteFile("Movies/a.txt", "A");
        var service = _fixture.CreateService();
        var preview = await service.PreviewAsync(
            _fixture.Command("/Movies", ["a.txt"], _fixture.Rules("renamed", "txt")),
            CancellationToken.None);
        var operation = await service.ExecuteAsync(preview.PlanId, CancellationToken.None);
        _fixture.Clock.Advance(TimeSpan.FromMinutes(31));

        await Assert.ThrowsAsync<RenamePlanExpiredException>(() =>
            service.UndoAsync(operation.OperationId, CancellationToken.None).AsTask());

        Assert.True(_fixture.EntryExists("Movies/renamed.txt"));
        Assert.False(_fixture.EntryExists("Movies/a.txt"));
    }

    public void Dispose() => _fixture.Dispose();
}

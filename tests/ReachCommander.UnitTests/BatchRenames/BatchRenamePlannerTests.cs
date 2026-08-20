using ReachCommander.Application.BatchRenames;
using ReachCommander.Application.Files;
using ReachCommander.UnitTests.Support;

namespace ReachCommander.UnitTests.BatchRenames;

public sealed class BatchRenamePlannerTests : IDisposable
{
    private readonly BatchRenameTestFixture _fixture = new();

    [Fact]
    public async Task Preview_returns_complete_new_names_in_request_order()
    {
        _fixture.WriteFile("Movies/holiday-photo.jpg", "photo");
        _fixture.CreateDirectory("Movies/Drafts");
        var planner = _fixture.CreatePlanner();

        var preview = await planner.PreviewAsync(new BatchRenamePreviewCommand(
            "media",
            "/Movies",
            ["/Movies/holiday-photo.jpg", "/Movies/Drafts"],
            _fixture.Rules("Archive-[C]", "[E]", counterDigits: 3)),
            CancellationToken.None);

        Assert.Equal(["Archive-001.jpg", "Archive-002"], preview.Rows.Select(row => row.NewName));
        Assert.All(preview.Rows, row => Assert.Equal(BatchRenamePreviewStatus.Ready, row.Status));
        Assert.Equal(2, preview.ChangedCount);
        Assert.True(preview.CanExecute);
    }

    [Fact]
    public async Task Preview_marks_every_duplicate_and_existing_destination_as_conflict()
    {
        _fixture.WriteFile("Movies/a.txt", "a");
        _fixture.WriteFile("Movies/b.txt", "b");
        _fixture.WriteFile("Movies/taken.txt", "occupied");
        var planner = _fixture.CreatePlanner();

        var duplicate = await planner.PreviewAsync(_fixture.Command(
            "/Movies", ["a.txt", "b.txt"], _fixture.Rules("same", "txt")), CancellationToken.None);
        var occupied = await planner.PreviewAsync(_fixture.Command(
            "/Movies", ["a.txt"], _fixture.Rules("taken", "txt")), CancellationToken.None);

        Assert.All(duplicate.Rows, row => Assert.Equal(BatchRenamePreviewStatus.Conflict, row.Status));
        Assert.Equal(BatchRenamePreviewStatus.Conflict, Assert.Single(occupied.Rows).Status);
        Assert.False(duplicate.CanExecute);
        Assert.False(occupied.CanExecute);
    }

    [Fact]
    public async Task Preview_allows_swaps_and_case_only_changes()
    {
        _fixture.WriteFile("Movies/a.txt", "a");
        _fixture.WriteFile("Movies/A.TXT.target", "target");
        var planner = _fixture.CreatePlanner();

        var casing = await planner.PreviewAsync(_fixture.Command(
            "/Movies", ["a.txt"], _fixture.Rules("A", "TXT")), CancellationToken.None);

        Assert.Equal(BatchRenamePreviewStatus.Ready, Assert.Single(casing.Rows).Status);
        Assert.True(casing.CanExecute);
    }

    [Fact]
    public async Task Preview_marks_unchanged_and_invalid_names_without_execution()
    {
        _fixture.WriteFile("Movies/a.txt", "a");
        var planner = _fixture.CreatePlanner();

        var unchanged = await planner.PreviewAsync(
            _fixture.Command("/Movies", ["a.txt"]), CancellationToken.None);
        var invalid = await planner.PreviewAsync(_fixture.Command(
            "/Movies", ["a.txt"], _fixture.Rules("bad/name", "txt")), CancellationToken.None);

        Assert.Equal(BatchRenamePreviewStatus.Unchanged, Assert.Single(unchanged.Rows).Status);
        Assert.Equal(BatchRenamePreviewStatus.Invalid, Assert.Single(invalid.Rows).Status);
        Assert.False(unchanged.CanExecute);
        Assert.False(invalid.CanExecute);
    }

    [Fact]
    public async Task Preview_blocks_read_only_sources_and_non_child_or_symbolic_link_entries()
    {
        _fixture.WriteFile("Movies/a.txt", "a");
        var readOnlyPlanner = _fixture.CreatePlanner(sourceReadOnly: true);
        await Assert.ThrowsAsync<SourceReadOnlyException>(() =>
            readOnlyPlanner.PreviewAsync(
                _fixture.Command("/Movies", ["a.txt"]),
                CancellationToken.None).AsTask());

        var planner = _fixture.CreatePlanner();
        await Assert.ThrowsAsync<InvalidLogicalPathException>(() =>
            planner.PreviewAsync(
                _fixture.Command("/Movies", ["../outside.txt"]),
                CancellationToken.None).AsTask());

        _fixture.WriteFile("Movies/link.txt", "target");
        _fixture.MarkEntryAsSymbolicLink("Movies/link.txt");
        var symbolicLink = await planner.PreviewAsync(
            _fixture.Command("/Movies", ["link.txt"]), CancellationToken.None);
        Assert.Equal(BatchRenamePreviewStatus.Invalid, Assert.Single(symbolicLink.Rows).Status);
        Assert.False(symbolicLink.CanExecute);
    }

    [Fact]
    public async Task Preview_limits_batches_and_expires_plans_after_ten_minutes()
    {
        var planner = _fixture.CreatePlanner(maxEntries: 2);
        await Assert.ThrowsAsync<BatchTooLargeException>(() =>
            planner.PreviewAsync(
                _fixture.Command("/Movies", ["a", "b", "c"]),
                CancellationToken.None).AsTask());

        _fixture.WriteFile("Movies/a.txt", "A");
        var preview = await planner.PreviewAsync(
            _fixture.Command("/Movies", ["a.txt"], _fixture.Rules("renamed", "txt")),
            CancellationToken.None);
        _fixture.Clock.Advance(TimeSpan.FromMinutes(11));

        Assert.Throws<RenamePlanExpiredException>(() =>
            _fixture.PlanStore.GetRequiredPlan(preview.PlanId));
    }

    [Fact]
    public async Task Revalidate_rejects_a_changed_source_entry_before_mutation()
    {
        _fixture.WriteFile("Movies/a.txt", "A");
        var planner = _fixture.CreatePlanner();
        var preview = await planner.PreviewAsync(
            _fixture.Command("/Movies", ["a.txt"], _fixture.Rules("renamed", "txt")),
            CancellationToken.None);
        _fixture.WriteFile("Movies/a.txt", "changed and longer");

        await Assert.ThrowsAsync<RenamePlanStaleException>(() =>
            planner.RevalidateAsync(
                _fixture.PlanStore.GetRequiredPlan(preview.PlanId),
                CancellationToken.None).AsTask());
    }

    public void Dispose() => _fixture.Dispose();
}

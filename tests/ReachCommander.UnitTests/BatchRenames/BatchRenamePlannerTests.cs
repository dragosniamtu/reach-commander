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

    [Fact]
    public async Task PreviewExact_treats_the_requested_file_name_as_literal()
    {
        _fixture.WriteFile("Movies/holiday.txt", "holiday");
        var planner = _fixture.CreatePlanner();

        var preview = await planner.PreviewExactAsync(new ExactRenamePreviewCommand(
            "media", "/Movies", "/Movies/holiday.txt", "[N]-literal.txt"),
            CancellationToken.None);

        var row = Assert.Single(preview.Rows);
        Assert.Equal("[N]-literal.txt", row.NewName);
        Assert.Equal(BatchRenamePreviewStatus.Ready, row.Status);
        Assert.True(preview.CanExecute);
    }

    [Fact]
    public async Task PreviewExact_supports_directories_and_refuses_an_occupied_name()
    {
        _fixture.CreateDirectory("Movies/Drafts");
        _fixture.CreateDirectory("Movies/Published");
        var planner = _fixture.CreatePlanner();

        var preview = await planner.PreviewExactAsync(new ExactRenamePreviewCommand(
            "media", "/Movies", "/Movies/Drafts", "Published"),
            CancellationToken.None);

        Assert.Equal(BatchRenamePreviewStatus.Conflict, Assert.Single(preview.Rows).Status);
        Assert.False(preview.CanExecute);
    }

    [Fact]
    public async Task PreviewExact_creates_a_plan_rejected_after_the_source_changes()
    {
        _fixture.WriteFile("Movies/original.txt", "original");
        var planner = _fixture.CreatePlanner();
        var preview = await planner.PreviewExactAsync(new ExactRenamePreviewCommand(
            "media", "/Movies", "/Movies/original.txt", "renamed.txt"),
            CancellationToken.None);
        _fixture.WriteFile("Movies/original.txt", "changed and longer");

        await Assert.ThrowsAsync<RenamePlanStaleException>(() => planner.RevalidateAsync(
            _fixture.PlanStore.GetRequiredPlan(preview.PlanId), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task PreviewExact_allows_a_case_only_name_change()
    {
        _fixture.WriteFile("Movies/Case.txt", "case");
        var preview = await _fixture.CreatePlanner().PreviewExactAsync(
            new ExactRenamePreviewCommand(
                "media", "/Movies", "/Movies/Case.txt", "case.txt"),
            CancellationToken.None);

        Assert.Equal(BatchRenamePreviewStatus.Ready, Assert.Single(preview.Rows).Status);
        Assert.True(preview.CanExecute);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("bad/name.txt")]
    [InlineData("CON")]
    public async Task PreviewExact_returns_a_non_executable_row_for_invalid_names(string newName)
    {
        _fixture.WriteFile("Movies/original.txt", "original");
        var preview = await _fixture.CreatePlanner().PreviewExactAsync(
            new ExactRenamePreviewCommand(
                "media", "/Movies", "/Movies/original.txt", newName),
            CancellationToken.None);

        Assert.Equal(BatchRenamePreviewStatus.Invalid, Assert.Single(preview.Rows).Status);
        Assert.False(preview.CanExecute);
    }

    [Fact]
    public async Task PreviewExact_reuses_read_only_and_symbolic_link_policy()
    {
        _fixture.WriteFile("Movies/link.txt", "target");
        _fixture.MarkEntryAsSymbolicLink("Movies/link.txt");
        var symbolic = await _fixture.CreatePlanner().PreviewExactAsync(
            new ExactRenamePreviewCommand(
                "media", "/Movies", "/Movies/link.txt", "renamed.txt"),
            CancellationToken.None);
        Assert.Equal(BatchRenamePreviewStatus.Invalid, Assert.Single(symbolic.Rows).Status);

        await Assert.ThrowsAsync<SourceReadOnlyException>(() =>
            _fixture.CreatePlanner(sourceReadOnly: true).PreviewExactAsync(
                new ExactRenamePreviewCommand(
                    "media", "/Movies", "/Movies/link.txt", "renamed.txt"),
                CancellationToken.None).AsTask());
    }

    public void Dispose() => _fixture.Dispose();
}

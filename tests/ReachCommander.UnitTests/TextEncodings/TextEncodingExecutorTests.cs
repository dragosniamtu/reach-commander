using System.Text;
using System.Text.Json;
using ReachCommander.Application.TextEncodings;
using ReachCommander.Infrastructure.TextEncodings;
using ReachCommander.UnitTests.Support;

namespace ReachCommander.UnitTests.TextEncodings;

public sealed class TextEncodingExecutorTests
{
    [Fact]
    public async Task Converts_windows_1250_to_bomless_utf8_and_preserves_exact_original_bytes()
    {
        using var fixture = new TextEncodingTestFixture();
        var original = Windows1250().GetBytes("Bună, ştii, ţară.\r\n");
        fixture.WriteBytes("TV/episode.srt", original);
        var plan = await PlanAsync(fixture, ["/TV/episode.srt"], TextEncodingKind.Windows1250);

        var operation = await ExecuteAsync(fixture, plan);

        Assert.Equal(TextEncodingOperationState.Completed, operation.State);
        Assert.Equal(original, File.ReadAllBytes(fixture.PhysicalPath("TV/episode_original.srt")));
        var converted = File.ReadAllBytes(fixture.PhysicalPath("TV/episode.srt"));
        Assert.False(converted.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
        Assert.Equal("Bună, ştii, ţară.\r\n", new UTF8Encoding(false, true).GetString(converted));
        Assert.Equal("/TV/episode_original.srt", Assert.Single(operation.Rows).BackupPath);
    }

    [Fact]
    public async Task Chooses_the_first_free_numbered_original_backup()
    {
        using var fixture = new TextEncodingTestFixture();
        fixture.WriteUtf8("TV/episode.srt", "current");
        fixture.WriteUtf8("TV/episode_original.srt", "old one");
        fixture.WriteUtf8("TV/episode_original (2).srt", "old two");
        var plan = await PlanAsync(fixture, ["/TV/episode.srt"]);

        var operation = await ExecuteAsync(fixture, plan);

        Assert.True(File.Exists(fixture.PhysicalPath("TV/episode_original (3).srt")));
        Assert.Equal("/TV/episode_original (3).srt", Assert.Single(operation.Rows).BackupPath);
    }

    [Fact]
    public async Task Fingerprint_change_after_preview_is_skipped_without_mutation()
    {
        using var fixture = new TextEncodingTestFixture();
        fixture.WriteUtf8("TV/episode.srt", "previewed");
        var plan = await PlanAsync(fixture, ["/TV/episode.srt"]);
        fixture.WriteUtf8("TV/episode.srt", "changed after preview");

        var operation = await ExecuteAsync(fixture, plan);

        var row = Assert.Single(operation.Rows);
        Assert.Equal(TextEncodingRowResult.Skipped, row.Result);
        Assert.Equal("text_file_stale", row.Code);
        Assert.Equal("changed after preview", File.ReadAllText(fixture.PhysicalPath("TV/episode.srt")));
        Assert.False(File.Exists(fixture.PhysicalPath("TV/episode_original.srt")));
    }

    [Fact]
    public async Task Staging_write_failure_leaves_original_untouched()
    {
        using var fixture = new TextEncodingTestFixture();
        fixture.WriteUtf8("TV/episode.srt", "original");
        var plan = await PlanAsync(fixture, ["/TV/episode.srt"]);
        var fileSystem = fixture.CreateInjectedFileSystem(failWrites: true);

        var operation = await ExecuteAsync(fixture, plan, fileSystem);

        Assert.Equal("original", File.ReadAllText(fixture.PhysicalPath("TV/episode.srt")));
        Assert.False(File.Exists(fixture.PhysicalPath("TV/episode_original.srt")));
        Assert.Equal(TextEncodingRowResult.Failed, Assert.Single(operation.Rows).Result);
        Assert.Empty(Directory.EnumerateFiles(
            fixture.PhysicalPath("TV"),
            ".reachcommander-operation-encoding-*.partial"));
    }

    [Fact]
    public async Task Publish_failure_restores_backup_to_original_name()
    {
        using var fixture = new TextEncodingTestFixture();
        fixture.WriteUtf8("TV/episode.srt", "original");
        var plan = await PlanAsync(fixture, ["/TV/episode.srt"]);
        var fileSystem = fixture.CreateInjectedFileSystem(failMoveCalls: [2]);

        var operation = await ExecuteAsync(fixture, plan, fileSystem);

        Assert.Equal("original", File.ReadAllText(fixture.PhysicalPath("TV/episode.srt")));
        Assert.False(File.Exists(fixture.PhysicalPath("TV/episode_original.srt")));
        var row = Assert.Single(operation.Rows);
        Assert.Equal(TextEncodingRowResult.Failed, row.Result);
        Assert.Equal("text_conversion_failed", row.Code);
    }

    [Fact]
    public async Task Rollback_failure_reports_recovery_required_with_logical_names_only()
    {
        using var fixture = new TextEncodingTestFixture();
        fixture.WriteUtf8("TV/episode.srt", "original");
        var plan = await PlanAsync(fixture, ["/TV/episode.srt"]);
        var fileSystem = fixture.CreateInjectedFileSystem(failMoveCalls: [2, 3]);

        var operation = await ExecuteAsync(fixture, plan, fileSystem);

        Assert.Equal(TextEncodingOperationState.Failed, operation.State);
        Assert.Equal("text_encoding_recovery_required", operation.ErrorCode);
        var row = Assert.Single(operation.Rows);
        Assert.Equal(TextEncodingRowResult.RecoveryRequired, row.Result);
        Assert.Equal("/TV/episode_original.srt", row.BackupPath);
        Assert.DoesNotContain(fixture.SourceRoot, JsonSerializer.Serialize(operation));
        Assert.True(File.Exists(fixture.PhysicalPath("TV/episode_original.srt")));
    }

    [Fact]
    public async Task Cancellation_after_first_file_does_not_begin_second_file()
    {
        using var fixture = new TextEncodingTestFixture();
        fixture.WriteUtf8("TV/one.srt", "one");
        fixture.WriteUtf8("TV/two.srt", "two");
        var plan = await PlanAsync(fixture, ["/TV/one.srt", "/TV/two.srt"]);
        var store = new TextEncodingOperationStore(fixture.Clock);
        var operationId = Guid.NewGuid();
        store.Create(operationId, plan.Entries);
        var fileSystem = fixture.CreateInjectedFileSystem(
            afterSuccessfulMove: (call, _, _) =>
            {
                if (call == 2)
                {
                    store.RequestCancellation(operationId);
                }
            });

        await fixture.CreateExecutor(store, fileSystem).RunAsync(
            plan,
            operationId,
            CancellationToken.None);
        var operation = store.GetRequired(operationId);

        Assert.Equal(TextEncodingOperationState.Cancelled, operation.State);
        Assert.Equal(TextEncodingRowResult.Converted, operation.Rows[0].Result);
        Assert.Equal(TextEncodingRowResult.Pending, operation.Rows[1].Result);
        Assert.Equal("two", File.ReadAllText(fixture.PhysicalPath("TV/two.srt")));
        Assert.False(File.Exists(fixture.PhysicalPath("TV/two_original.srt")));
    }

    [Fact]
    public async Task Symbolic_link_introduced_after_preview_is_rejected()
    {
        using var fixture = new TextEncodingTestFixture();
        fixture.WriteUtf8("TV/episode.srt", "original");
        var plan = await PlanAsync(fixture, ["/TV/episode.srt"]);
        fixture.MarkAsSymbolicLink("TV/episode.srt");

        var operation = await ExecuteAsync(fixture, plan);

        var row = Assert.Single(operation.Rows);
        Assert.Equal(TextEncodingRowResult.Skipped, row.Result);
        Assert.Equal("text_symbolic_link_rejected", row.Code);
        Assert.False(File.Exists(fixture.PhysicalPath("TV/episode_original.srt")));
    }

    private static async Task<StoredTextEncodingPlan> PlanAsync(
        TextEncodingTestFixture fixture,
        IReadOnlyList<string> paths,
        TextEncodingKind sourceEncoding = TextEncodingKind.Auto)
    {
        var preview = await fixture.Planner.PreviewAsync(new(
            "media",
            paths,
            sourceEncoding,
            TextEncodingKind.Utf8),
            CancellationToken.None);
        return fixture.PlanStore.Get(preview.PlanId);
    }

    private static async Task<TextEncodingOperation> ExecuteAsync(
        TextEncodingTestFixture fixture,
        StoredTextEncodingPlan plan,
        ITextEncodingFileSystem? fileSystem = null)
    {
        var store = new TextEncodingOperationStore(fixture.Clock);
        var operationId = Guid.NewGuid();
        store.Create(operationId, plan.Entries);
        await fixture.CreateExecutor(store, fileSystem).RunAsync(
            plan,
            operationId,
            CancellationToken.None);
        return store.GetRequired(operationId);
    }

    private static Encoding Windows1250()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1250);
    }
}

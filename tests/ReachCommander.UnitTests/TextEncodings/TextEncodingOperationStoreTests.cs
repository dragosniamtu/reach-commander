using ReachCommander.Application.TextEncodings;
using ReachCommander.Infrastructure.TextEncodings;
using ReachCommander.UnitTests.Support;

namespace ReachCommander.UnitTests.TextEncodings;

public sealed class TextEncodingOperationStoreTests
{
    [Fact]
    public void Operation_moves_from_queued_to_running_to_completed_with_monotonic_progress()
    {
        var clock = Clock();
        var store = new TextEncodingOperationStore(clock);
        var operationId = Guid.NewGuid();
        store.Create(operationId, [Entry("/TV/one.srt"), Entry("/TV/two.srt")]);

        var queued = store.GetRequired(operationId);
        Assert.Equal(TextEncodingOperationState.Queued, queued.State);
        Assert.True(queued.CanCancel);
        Assert.Equal(0, queued.Percent);

        store.MarkRunning(operationId);
        store.BeginFile(operationId, 0, "one.srt");
        var afterFirst = store.CompleteFile(
            operationId,
            0,
            TextEncodingRowResult.Converted,
            "/TV/one_original.srt",
            code: null,
            detail: null);

        Assert.Equal(1, afterFirst.CompletedFiles);
        Assert.Equal(50, afterFirst.Percent);
        Assert.True(afterFirst.CanCancel);

        store.BeginFile(operationId, 1, "two.srt");
        store.CompleteFile(
            operationId,
            1,
            TextEncodingRowResult.Converted,
            "/TV/two_original.srt",
            code: null,
            detail: null);
        var completed = store.MarkTerminal(operationId, TextEncodingOperationState.Completed);

        Assert.Equal(TextEncodingOperationState.Completed, completed.State);
        Assert.Equal(100, completed.Percent);
        Assert.False(completed.CanCancel);
    }

    [Fact]
    public void Completed_request_becomes_completed_with_errors_when_a_row_failed()
    {
        var store = new TextEncodingOperationStore(Clock());
        var operationId = Guid.NewGuid();
        store.Create(operationId, [Entry("/TV/one.srt")]);
        store.MarkRunning(operationId);
        store.CompleteFile(
            operationId,
            0,
            TextEncodingRowResult.Failed,
            backupPath: null,
            "text_conversion_failed",
            "The file was not changed.");

        var completed = store.MarkTerminal(operationId, TextEncodingOperationState.Completed);

        Assert.Equal(TextEncodingOperationState.CompletedWithErrors, completed.State);
        Assert.Equal(100, completed.Percent);
    }

    [Fact]
    public void Cancellation_changes_state_signals_token_and_can_finish_cancelled()
    {
        var store = new TextEncodingOperationStore(Clock());
        var operationId = Guid.NewGuid();
        store.Create(operationId, [Entry("/TV/one.srt")]);
        var cancellation = store.GetCancellationToken(operationId);

        var requested = store.RequestCancellation(operationId);

        Assert.Equal(TextEncodingOperationState.CancelRequested, requested.State);
        Assert.False(requested.CanCancel);
        Assert.True(cancellation.IsCancellationRequested);
        var cancelled = store.MarkTerminal(operationId, TextEncodingOperationState.Cancelled);
        Assert.Equal(TextEncodingOperationState.Cancelled, cancelled.State);
    }

    [Fact]
    public void Current_filename_is_stripped_and_bounded()
    {
        var store = new TextEncodingOperationStore(Clock());
        var operationId = Guid.NewGuid();
        store.Create(operationId, [Entry("/TV/one.srt")]);
        store.MarkRunning(operationId);

        var running = store.BeginFile(operationId, 0, $"/secret/{new string('x', 300)}.srt");

        Assert.DoesNotContain("secret", running.CurrentFileName, StringComparison.Ordinal);
        Assert.True(running.CurrentFileName!.Length <= 160);
    }

    [Fact]
    public void Terminal_state_is_idempotent()
    {
        var store = new TextEncodingOperationStore(Clock());
        var operationId = Guid.NewGuid();
        store.Create(operationId, [Entry("/TV/one.srt")]);
        store.MarkRunning(operationId);
        store.CompleteFile(
            operationId,
            0,
            TextEncodingRowResult.Converted,
            "/TV/one_original.srt",
            null,
            null);
        var completed = store.MarkTerminal(operationId, TextEncodingOperationState.Completed);

        var repeated = store.MarkTerminal(
            operationId,
            TextEncodingOperationState.Failed,
            "should_not_replace",
            "should not replace");

        Assert.Equal(completed.State, repeated.State);
        Assert.Equal(completed.ErrorCode, repeated.ErrorCode);
        Assert.Equal(completed.ErrorDetail, repeated.ErrorDetail);
        Assert.Equal(completed.Rows, repeated.Rows);
    }

    [Fact]
    public void Recovery_required_row_forces_failed_terminal_state()
    {
        var store = new TextEncodingOperationStore(Clock());
        var operationId = Guid.NewGuid();
        store.Create(operationId, [Entry("/TV/one.srt")]);
        store.MarkRunning(operationId);
        store.CompleteFile(
            operationId,
            0,
            TextEncodingRowResult.RecoveryRequired,
            "/TV/one_original.srt",
            "text_encoding_recovery_required",
            "Manual recovery is required.");

        var failed = store.MarkTerminal(operationId, TextEncodingOperationState.Completed);

        Assert.Equal(TextEncodingOperationState.Failed, failed.State);
        Assert.Equal("text_encoding_recovery_required", failed.ErrorCode);
    }

    [Fact]
    public void Store_keeps_only_one_hundred_newest_terminal_operations()
    {
        var store = new TextEncodingOperationStore(Clock());
        var operationIds = Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var operationId in operationIds)
        {
            store.Create(operationId, []);
            store.MarkRunning(operationId);
            store.MarkTerminal(operationId, TextEncodingOperationState.Completed);
        }

        var error = Assert.Throws<TextEncodingException>(() => store.GetRequired(operationIds[0]));
        Assert.Equal("text_encoding_operation_not_found", error.Code);
        Assert.Equal(TextEncodingOperationState.Completed, store.GetRequired(operationIds[^1]).State);
    }

    [Fact]
    public void Terminal_operation_expires_after_one_hour()
    {
        var clock = Clock();
        var store = new TextEncodingOperationStore(clock);
        var operationId = Guid.NewGuid();
        store.Create(operationId, []);
        store.MarkRunning(operationId);
        store.MarkTerminal(operationId, TextEncodingOperationState.Completed);

        clock.Advance(TimeSpan.FromHours(1));

        var error = Assert.Throws<TextEncodingException>(() => store.GetRequired(operationId));
        Assert.Equal("text_encoding_operation_expired", error.Code);
    }

    private static TextEncodingTestFixture.ManualTimeProvider Clock() =>
        new(new DateTimeOffset(2026, 9, 2, 8, 0, 0, TimeSpan.Zero));

    private static StoredTextEncodingEntry Entry(string logicalPath) => new(
        logicalPath,
        $"C:\\safe{logicalPath.Replace('/', '\\')}",
        "/TV",
        "C:\\safe\\TV",
        Path.GetFileName(logicalPath),
        new TextFileFingerprint(10, DateTimeOffset.UnixEpoch, FileAttributes.Normal, "abc"),
        TextEncodingKind.Utf8,
        TextEncodingKind.Utf8,
        TextEncodingPreviewStatus.Ready);
}

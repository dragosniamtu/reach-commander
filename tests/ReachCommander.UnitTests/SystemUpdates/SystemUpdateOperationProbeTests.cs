using ReachCommander.Application.Archives;
using ReachCommander.Application.FileOperations;
using ReachCommander.Infrastructure.Archives.Extraction;
using ReachCommander.Infrastructure.SystemUpdates;

namespace ReachCommander.UnitTests.SystemUpdates;

public sealed class SystemUpdateOperationProbeTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-25T10:00:00Z");

    [Theory]
    [InlineData(FileOperationPhase.Queued, true)]
    [InlineData(FileOperationPhase.Validating, true)]
    [InlineData(FileOperationPhase.Running, true)]
    [InlineData(FileOperationPhase.Cancelling, true)]
    [InlineData(FileOperationPhase.Completed, false)]
    [InlineData(FileOperationPhase.CompletedWithErrors, false)]
    [InlineData(FileOperationPhase.Cancelled, false)]
    [InlineData(FileOperationPhase.Failed, false)]
    [InlineData(FileOperationPhase.Interrupted, false)]
    public async Task File_operation_phase_has_expected_activity(
        FileOperationPhase phase,
        bool expected)
    {
        var probe = new SystemUpdateOperationProbe(
            new StubFileOperationService([Status(phase)]));

        Assert.Equal(expected, await probe.HasActiveOperationsAsync(default));
    }

    [Fact]
    public async Task Archive_operation_is_active_until_terminal()
    {
        var store = new ArchiveExtractionOperationStore(new FixedTimeProvider());
        var plan = new ArchiveExtractionPlan(
            "plan-1",
            Now,
            Now.AddMinutes(10),
            "downloads",
            "/sample.zip",
            null!,
            "/",
            [],
            [],
            [],
            "media",
            "/",
            "snapshot",
            [],
            [],
            true);
        store.Create("operation-1", plan);
        var probe = new SystemUpdateOperationProbe(new StubFileOperationService([]), store);

        Assert.True(await probe.HasActiveOperationsAsync(default));
        store.MarkCompleted("operation-1");
        Assert.False(await probe.HasActiveOperationsAsync(default));
    }

    private static FileOperationStatus Status(FileOperationPhase phase) => new(
        Guid.NewGuid(),
        FileOperationKind.Copy,
        phase,
        0,
        Now,
        Now,
        new FileOperationProgress(null, 0, 1, 0, 1, 0, null, TimeSpan.Zero, null),
        [],
        [],
        false);

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class StubFileOperationService(IReadOnlyList<FileOperationStatus> operations)
        : IFileOperationService
    {
        public Task<IReadOnlyList<FileOperationStatus>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult(operations);

        public Task<FileOperationPreview> PreviewAsync(
            FileOperationPreviewRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<FileOperationStatus> SubmitAsync(
            FileOperationSubmission request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<FileOperationStatus> GetAsync(
            Guid operationId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<FileOperationStatus> CancelAsync(
            Guid operationId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task AcknowledgeAsync(Guid operationId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}

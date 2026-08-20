using Microsoft.Extensions.Options;
using ReachCommander.Application.Archives;
using ReachCommander.Application.Files;
using ReachCommander.Application.Sources;
using ReachCommander.Domain.Archives;
using ReachCommander.Domain.Sources;
using ReachCommander.Infrastructure.Archives;
using ReachCommander.Infrastructure.Archives.Extraction;
using ReachCommander.Infrastructure.Archives.Volumes;
using ReachCommander.Infrastructure.Archives.Worker;
using ReachCommander.Infrastructure.Mutations;
using ReachCommander.Infrastructure.Security;
using ReachCommander.UnitTests.Support;

namespace ReachCommander.UnitTests.Archives;

public sealed class ArchiveExtractionCoordinatorTests
{
    [Fact]
    public async Task Success_stages_before_exposing_final_names_and_records_the_full_state_sequence()
    {
        using var fixture = new CoordinatorFixture();
        fixture.Worker.Block();

        var accepted = await fixture.Service.ExecuteAsync("plan-one", default);
        await fixture.Worker.Started.Task;

        Assert.Equal(ArchiveExtractionState.Extracting, fixture.Operations.GetRequired(accepted.OperationId).State);
        Assert.False(File.Exists(Path.Combine(fixture.MediaRoot, "photo.txt")));
        Assert.True(Directory.Exists(fixture.StagingPath(accepted.OperationId)));

        fixture.Worker.Release();
        var completed = await fixture.Operations.WaitForTerminalAsync(accepted.OperationId, default);

        Assert.Equal(ArchiveExtractionState.Completed, completed.State);
        Assert.Equal("abc", await File.ReadAllTextAsync(Path.Combine(fixture.MediaRoot, "photo.txt")));
        Assert.False(Directory.Exists(fixture.StagingPath(accepted.OperationId)));
        Assert.Equal(
            [
                ArchiveExtractionState.Queued,
                ArchiveExtractionState.Extracting,
                ArchiveExtractionState.Finalizing,
                ArchiveExtractionState.Completed,
            ],
            fixture.Operations.GetStateHistory(accepted.OperationId));
    }

    [Fact]
    public async Task Empty_directory_only_plan_completes_without_launching_a_worker()
    {
        using var fixture = new CoordinatorFixture();
        fixture.AddEmptyDirectoryPlan();

        var operation = await fixture.Service.ExecuteAsync("plan-empty-directory", default);
        var completed = await fixture.Operations.WaitForTerminalAsync(operation.OperationId, default);

        Assert.Equal(ArchiveExtractionState.Completed, completed.State);
        Assert.True(Directory.Exists(Path.Combine(fixture.MediaRoot, "Empty")));
        Assert.Equal(0, fixture.Worker.InvocationCount);
    }

    [Fact]
    public void Staging_creation_is_exclusive_and_cleanup_requires_its_ownership_identity()
    {
        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.Path, ".reachcommander-extract-operation.partial");
        var fileSystem = new LocalArchiveExtractionRuntimeFileSystem();
        using var identity = fileSystem.CreateOwnedStagingDirectory(root);

        Assert.True(fileSystem.VerifyOwnedStaging(identity));
        Assert.Throws<IOException>(() => fileSystem.CreateOwnedStagingDirectory(root));

        fileSystem.DeleteOwnedDirectoryTree(identity);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void Cleanup_refuses_a_replacement_directory_even_if_it_uses_the_same_path()
    {
        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.Path, ".reachcommander-extract-operation.partial");
        var original = Path.Combine(temporary.Path, "original-owned-staging");
        var fileSystem = new LocalArchiveExtractionRuntimeFileSystem();
        using var identity = fileSystem.CreateOwnedStagingDirectory(root);
        if (OperatingSystem.IsWindows())
        {
            identity.Dispose();
        }

        Directory.Move(root, original);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "unrelated.txt"), "keep");

        Assert.Throws<IOException>(() => fileSystem.DeleteOwnedDirectoryTree(identity));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(root, "unrelated.txt")));
    }

    [Fact]
    public async Task Partial_staging_identity_failure_is_cleaned_or_reported_for_recovery()
    {
        using var fixture = new CoordinatorFixture(failAfterStagingIdentity: true);

        var operation = await fixture.Service.ExecuteAsync("plan-one", default);
        var result = await fixture.Operations.WaitForTerminalAsync(operation.OperationId, default);

        Assert.Equal(ArchiveExtractionState.Failed, result.State);
        Assert.False(Directory.Exists(fixture.StagingPath(operation.OperationId)));
        Assert.Equal(0, fixture.Worker.InvocationCount);
    }

    [Fact]
    public async Task Execute_is_idempotent_and_rejects_immediate_capacity_overflow()
    {
        using var fixture = new CoordinatorFixture();
        fixture.Worker.Block();
        fixture.AddPlan("plan-two", "second.txt", workerIndex: 1);

        var first = await fixture.Service.ExecuteAsync("plan-one", default);
        await fixture.Worker.Started.Task;
        var repeated = await fixture.Service.ExecuteAsync("plan-one", default);

        Assert.Equal(first.OperationId, repeated.OperationId);
        await Assert.ThrowsAsync<ArchiveCapacityReachedException>(() =>
            fixture.Service.ExecuteAsync("plan-two", default).AsTask());

        fixture.Worker.Release();
        await fixture.Operations.WaitForTerminalAsync(first.OperationId, default);
        Assert.Equal(1, fixture.Worker.InvocationCount);
    }

    [Fact]
    public async Task Changed_source_fingerprint_or_destination_snapshot_fails_before_staging()
    {
        using var staleSource = new CoordinatorFixture();
        staleSource.Resolver.Fingerprint = new("changed");
        var sourceOperation = await staleSource.Service.ExecuteAsync("plan-one", default);
        var sourceResult = await staleSource.Operations.WaitForTerminalAsync(
            sourceOperation.OperationId,
            default);

        Assert.Equal("archive_plan_stale", sourceResult.ErrorCode);
        Assert.False(Directory.Exists(staleSource.StagingPath(sourceOperation.OperationId)));
        Assert.Equal(0, staleSource.Worker.InvocationCount);

        using var changedDestination = new CoordinatorFixture();
        await File.WriteAllTextAsync(Path.Combine(changedDestination.MediaRoot, "external.txt"), "x");
        var destinationOperation = await changedDestination.Service.ExecuteAsync("plan-one", default);
        var destinationResult = await changedDestination.Operations.WaitForTerminalAsync(
            destinationOperation.OperationId,
            default);

        Assert.Equal("archive_destination_changed", destinationResult.ErrorCode);
        Assert.Equal(0, changedDestination.Worker.InvocationCount);
    }

    [Fact]
    public async Task Runtime_limits_and_cancellation_remove_staging_and_final_names()
    {
        using var limited = new CoordinatorFixture(new ArchiveOptions
        {
            MaxSingleExtractedFileBytes = 2,
            MaxTotalExtractedBytes = 10,
        });
        var limitedOperation = await limited.Service.ExecuteAsync("plan-one", default);
        var limitedResult = await limited.Operations.WaitForTerminalAsync(
            limitedOperation.OperationId,
            default);

        Assert.Equal("archive_limit_exceeded", limitedResult.ErrorCode);
        Assert.False(File.Exists(Path.Combine(limited.MediaRoot, "photo.txt")));
        Assert.False(Directory.Exists(limited.StagingPath(limitedOperation.OperationId)));

        using var cancelled = new CoordinatorFixture();
        cancelled.Worker.Block();
        var cancelledOperation = await cancelled.Service.ExecuteAsync("plan-one", default);
        await cancelled.Worker.Started.Task;

        await cancelled.Service.CancelAsync(cancelledOperation.OperationId, default);
        var cancelledResult = await cancelled.Operations.WaitForTerminalAsync(
            cancelledOperation.OperationId,
            default);

        Assert.Equal(ArchiveExtractionState.Cancelled, cancelledResult.State);
        Assert.False(File.Exists(Path.Combine(cancelled.MediaRoot, "photo.txt")));
        Assert.False(Directory.Exists(cancelled.StagingPath(cancelledOperation.OperationId)));
    }

    [Fact]
    public async Task Cancellation_is_ignored_once_finalization_has_started()
    {
        using var moveGate = new MoveGate();
        using var fixture = new CoordinatorFixture(moveGate: moveGate);

        var operation = await fixture.Service.ExecuteAsync("plan-one", default);
        await moveGate.Entered.Task;

        var cancellationResult = await fixture.Service.CancelAsync(operation.OperationId, default);
        Assert.Equal(ArchiveExtractionState.Finalizing, cancellationResult.State);
        Assert.False(cancellationResult.CanCancel);

        moveGate.Release.Set();
        var completed = await fixture.Operations.WaitForTerminalAsync(operation.OperationId, default);
        Assert.Equal(ArchiveExtractionState.Completed, completed.State);
        Assert.True(File.Exists(Path.Combine(fixture.MediaRoot, "photo.txt")));
    }

    [Fact]
    public async Task Cancellation_accepted_at_final_revalidation_stops_before_finalization()
    {
        using var gate = new FinalRevalidationGate();
        using var fixture = new CoordinatorFixture(revalidationGate: gate);

        var operation = await fixture.Service.ExecuteAsync("plan-one", default);
        await gate.Entered.Task;
        var cancellation = await fixture.Service.CancelAsync(operation.OperationId, default);

        Assert.Equal(ArchiveExtractionState.Extracting, cancellation.State);
        Assert.True(cancellation.CanCancel);
        gate.Release.Set();
        var result = await fixture.Operations.WaitForTerminalAsync(operation.OperationId, default);
        Assert.Equal(ArchiveExtractionState.Cancelled, result.State);
        Assert.False(File.Exists(Path.Combine(fixture.MediaRoot, "photo.txt")));
        Assert.False(Directory.Exists(fixture.StagingPath(operation.OperationId)));
    }

    [Fact]
    public async Task Revalidation_catches_changes_after_streaming_and_worker_crashes_cleanup()
    {
        using var destinationChange = new CoordinatorFixture();
        destinationChange.Worker.AfterExtraction = () =>
            File.WriteAllText(Path.Combine(destinationChange.MediaRoot, "external.txt"), "x");
        var changedOperation = await destinationChange.Service.ExecuteAsync("plan-one", default);
        var changed = await destinationChange.Operations.WaitForTerminalAsync(
            changedOperation.OperationId,
            default);

        Assert.Equal("archive_destination_changed", changed.ErrorCode);
        Assert.False(File.Exists(Path.Combine(destinationChange.MediaRoot, "photo.txt")));
        Assert.False(Directory.Exists(destinationChange.StagingPath(changedOperation.OperationId)));

        using var crashed = new CoordinatorFixture();
        crashed.Worker.Failure = new ArchiveWorkerFailedException();
        var crashedOperation = await crashed.Service.ExecuteAsync("plan-one", default);
        var failed = await crashed.Operations.WaitForTerminalAsync(crashedOperation.OperationId, default);

        Assert.Equal("archive_worker_failed", failed.ErrorCode);
        Assert.False(Directory.Exists(crashed.StagingPath(crashedOperation.OperationId)));
    }

    [Fact]
    public async Task Progress_rejects_regressions_and_operation_ids_are_256_bit_base64url()
    {
        using var fixture = new CoordinatorFixture();
        fixture.Worker.Block();
        var operation = await fixture.Service.ExecuteAsync("plan-one", default);
        await fixture.Worker.Started.Task;

        fixture.Operations.ReportProgress(operation.OperationId, 0, 1, "photo.txt");
        Assert.Throws<InvalidOperationException>(() =>
            fixture.Operations.ReportProgress(operation.OperationId, 0, 0, "photo.txt"));
        Assert.Null(fixture.Operations.GetRequired(operation.OperationId).Percent);

        var generated = new ArchiveOperationIdGenerator().CreateId();
        Assert.Equal(43, generated.Length);
        Assert.DoesNotContain('=', generated);
        Assert.DoesNotContain('+', generated);
        Assert.DoesNotContain('/', generated);

        await fixture.Service.CancelAsync(operation.OperationId, default);
        await fixture.Operations.WaitForTerminalAsync(operation.OperationId, default);
    }

    [Fact]
    public async Task Partial_finalization_is_compensated_or_preserved_for_recovery()
    {
        using var compensated = new CoordinatorFixture(moveFailure: new MoveFailurePolicy(
            FailSecondFinalMove: true,
            FailCompensation: false));
        compensated.AddSecondRoot();
        var compensatedOperation = await compensated.Service.ExecuteAsync("plan-two-roots", default);
        var compensatedResult = await compensated.Operations.WaitForTerminalAsync(
            compensatedOperation.OperationId,
            default);

        Assert.Equal(ArchiveExtractionState.Failed, compensatedResult.State);
        Assert.Equal(ArchiveCompensationState.Succeeded, compensatedResult.CompensationState);
        Assert.False(File.Exists(Path.Combine(compensated.MediaRoot, "one.txt")));
        Assert.False(File.Exists(Path.Combine(compensated.MediaRoot, "two.txt")));
        Assert.False(Directory.Exists(compensated.StagingPath(compensatedOperation.OperationId)));

        using var recovery = new CoordinatorFixture(moveFailure: new MoveFailurePolicy(
            FailSecondFinalMove: true,
            FailCompensation: true));
        recovery.AddSecondRoot();
        var recoveryOperation = await recovery.Service.ExecuteAsync("plan-two-roots", default);
        var recoveryResult = await recovery.Operations.WaitForTerminalAsync(
            recoveryOperation.OperationId,
            default);

        Assert.Equal(ArchiveExtractionState.RecoveryRequired, recoveryResult.State);
        Assert.Equal(ArchiveCompensationState.Failed, recoveryResult.CompensationState);
        Assert.Equal(["one.txt"], recoveryResult.RecoveryNames);
        Assert.True(Directory.Exists(recovery.StagingPath(recoveryOperation.OperationId)));
        Assert.DoesNotContain(recovery.MediaRoot, string.Join('|', recoveryResult.RecoveryNames));
    }

    private sealed class CoordinatorFixture : IDisposable
    {
        private readonly TemporaryDirectory _temporary = new();
        private readonly ArchiveOptions _options;
        private readonly ArchiveExtractionPlanStore _plans;
        private readonly PlannedWorker _worker;
        private readonly IArchiveExtractionRuntimeFileSystem _fileSystem;
        private readonly ResolvedArchivePartSet _partSet;

        public CoordinatorFixture(
            ArchiveOptions? options = null,
            MoveFailurePolicy? moveFailure = null,
            MoveGate? moveGate = null,
            FinalRevalidationGate? revalidationGate = null,
            bool failAfterStagingIdentity = false)
        {
            _options = options ?? new ArchiveOptions();
            DownloadsRoot = _temporary.CreateDirectory("downloads");
            MediaRoot = _temporary.CreateDirectory("media");
            var archivePath = Path.Combine(DownloadsRoot, "photos.zip");
            File.WriteAllText(archivePath, "archive");
            var file = new FileInfo(archivePath);
            var part = new ResolvedArchivePart(
                "/photos.zip",
                file.FullName,
                file.Length,
                new DateTimeOffset(file.LastWriteTimeUtc));
            _partSet = new(
                ArchiveFormat.Zip,
                "/photos.zip",
                [part],
                ArchiveVolumeFingerprint.Create("downloads", "/photos.zip", [part]));
            Resolver = new FakePartResolver(_partSet);
            var sources = new FakeSourceCatalog(DownloadsRoot, MediaRoot);
            var pathSecurity = new PathSecurityService(sources);
            var localFileSystem = new LocalArchiveExtractionRuntimeFileSystem();
            _fileSystem = moveFailure is not null
                ? new FailingMoveFileSystem(localFileSystem, moveFailure)
                : moveGate is not null
                    ? new GatedMoveFileSystem(localFileSystem, moveGate)
                    : revalidationGate is not null
                        ? new GatedRevalidationFileSystem(localFileSystem, revalidationGate)
                        : failAfterStagingIdentity
                            ? new FailingStagingCreationFileSystem(localFileSystem)
                            : localFileSystem;
            var clock = TimeProvider.System;
            _plans = new ArchiveExtractionPlanStore(clock);
            Operations = new ArchiveExtractionOperationStore(clock);
            _worker = new PlannedWorker();
            Worker = _worker;
            var coordinator = new ArchiveExtractionCoordinator(
                Resolver,
                pathSecurity,
                _fileSystem,
                new DirectoryMutationLock(),
                _worker,
                Operations,
                Options.Create(_options),
                clock);
            Service = new ArchiveExtractionService(
                null!,
                _plans,
                Operations,
                coordinator,
                new SequentialOperationIdGenerator(),
                Options.Create(_options));
            AddPlan("plan-one", "photo.txt", workerIndex: 0);
        }

        public string DownloadsRoot { get; }

        public string MediaRoot { get; }

        public FakePartResolver Resolver { get; }

        public PlannedWorker Worker { get; }

        public ArchiveExtractionOperationStore Operations { get; }

        public ArchiveExtractionService Service { get; }

        public string StagingPath(string operationId) =>
            Path.Combine(MediaRoot, $".reachcommander-extract-{operationId}.partial");

        public void AddPlan(string planId, string outputName, int workerIndex)
        {
            var entries = _fileSystem.ListChildren(MediaRoot);
            _plans.Add(new ArchiveExtractionPlan(
                planId,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMinutes(10),
                "downloads",
                "/photos.zip",
                _partSet,
                "/",
                [outputName],
                [new PlannedArchiveFile(workerIndex, $"/{outputName}", outputName, null, 1, null)],
                [],
                "media",
                "/",
                ArchiveExtractionPlanner.CreateDestinationSnapshot(entries),
                [],
                [],
                true));
        }

        public void AddSecondRoot()
        {
            _plans.Add(new ArchiveExtractionPlan(
                "plan-two-roots",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMinutes(10),
                "downloads",
                "/photos.zip",
                _partSet,
                "/",
                ["one.txt", "two.txt"],
                [
                    new PlannedArchiveFile(0, "/one.txt", "one.txt", 3, 1, null),
                    new PlannedArchiveFile(1, "/two.txt", "two.txt", 3, 1, null),
                ],
                [],
                "media",
                "/",
                ArchiveExtractionPlanner.CreateDestinationSnapshot(_fileSystem.ListChildren(MediaRoot)),
                [],
                [],
                true));
            _worker.Files = new Dictionary<int, byte[]>
            {
                [0] = "one"u8.ToArray(),
                [1] = "two"u8.ToArray(),
            };
        }

        public void AddEmptyDirectoryPlan()
        {
            _plans.Add(new ArchiveExtractionPlan(
                "plan-empty-directory",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMinutes(10),
                "downloads",
                "/photos.zip",
                _partSet,
                "/",
                ["Empty"],
                [],
                ["Empty"],
                "media",
                "/",
                ArchiveExtractionPlanner.CreateDestinationSnapshot(_fileSystem.ListChildren(MediaRoot)),
                [],
                [],
                true));
        }

        public void Dispose() => _temporary.Dispose();
    }

    private sealed class PlannedWorker : IArchiveWorkerClient
    {
        private TaskCompletionSource? _release;

        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int InvocationCount { get; private set; }

        public Dictionary<int, byte[]> Files { get; set; } = new()
        {
            [0] = "abc"u8.ToArray(),
            [1] = "second"u8.ToArray(),
        };

        public Action? AfterExtraction { get; set; }

        public Exception? Failure { get; set; }

        public void Block() => _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release?.TrySetResult();

        public ValueTask<ArchiveWorkerInspection> InspectAsync(
            ResolvedArchivePartSet partSet,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async ValueTask ExtractAsync(
            ResolvedArchivePartSet partSet,
            IReadOnlyList<int> entryIndexes,
            IArchiveEntrySink sink,
            CancellationToken cancellationToken)
        {
            InvocationCount++;
            Started.TrySetResult();
            if (_release is not null)
            {
                await _release.Task.WaitAsync(cancellationToken);
            }

            if (Failure is not null)
            {
                throw Failure;
            }

            long total = 0;
            var completed = 0;
            foreach (var index in entryIndexes.Order())
            {
                var content = Files[index];
                await sink.StartAsync(index, cancellationToken);
                await sink.WriteAsync(content, cancellationToken);
                await sink.EndAsync(index, content.Length, cancellationToken);
                total += content.Length;
                completed++;
                await sink.ProgressAsync(completed, total, cancellationToken);
            }

            AfterExtraction?.Invoke();
        }
    }

    private sealed class FakePartResolver(ResolvedArchivePartSet initial) : IArchivePartResolver
    {
        public ArchiveVolumeFingerprint Fingerprint { get; set; } = initial.Fingerprint;

        public ValueTask<ResolvedArchivePartSet> ResolveAsync(
            string sourceId,
            string archivePath,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(initial with { Fingerprint = Fingerprint });
    }

    private sealed class FakeSourceCatalog(string downloadsRoot, string mediaRoot) : ISourceCatalog
    {
        private readonly SourceDefinition[] _definitions =
        [
            new("downloads", "Downloads", downloadsRoot, false, true, false),
            new("media", "Media", mediaRoot, false, false, true),
        ];

        public ValueTask<IReadOnlyList<SourceDefinition>> GetDefinitionsAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<SourceDefinition>>(_definitions);

        public ValueTask<IReadOnlyList<SourceSnapshot>> GetSnapshotsAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<SourceDefinition> GetRequiredAsync(
            string sourceId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(_definitions.Single(source => source.Id == sourceId));
    }

    private sealed record MoveFailurePolicy(bool FailSecondFinalMove, bool FailCompensation);

    private sealed class MoveGate : IDisposable
    {
        public TaskCompletionSource Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualResetEventSlim Release { get; } = new(false);

        public void Dispose()
        {
            Release.Set();
            Release.Dispose();
        }
    }

    private sealed class FinalRevalidationGate : IDisposable
    {
        public TaskCompletionSource Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualResetEventSlim Release { get; } = new(false);

        public void Dispose()
        {
            Release.Set();
            Release.Dispose();
        }
    }

    private sealed class GatedRevalidationFileSystem(
        IArchiveExtractionRuntimeFileSystem inner,
        FinalRevalidationGate gate) : IArchiveExtractionRuntimeFileSystem
    {
        private int _gated;

        public bool DirectoryExists(string physicalPath) => inner.DirectoryExists(physicalPath);

        public IReadOnlyList<ArchiveDestinationEntry> ListChildren(string physicalDirectory)
        {
            var entries = inner.ListChildren(physicalDirectory);
            if (entries.Any(entry =>
                    entry.Name.StartsWith(".reachcommander-extract-", StringComparison.Ordinal) &&
                    entry.Name.EndsWith(".partial", StringComparison.Ordinal)) &&
                Interlocked.Exchange(ref _gated, 1) == 0)
            {
                gate.Entered.TrySetResult();
                gate.Release.Wait();
            }

            return entries;
        }

        public long? GetAvailableFreeSpace(string physicalDirectory) =>
            inner.GetAvailableFreeSpace(physicalDirectory);
        public IDisposable OpenReadShared(string physicalPath) => inner.OpenReadShared(physicalPath);
        public ArchiveStagingIdentity CreateOwnedStagingDirectory(string physicalPath) =>
            inner.CreateOwnedStagingDirectory(physicalPath);
        public bool VerifyOwnedStaging(ArchiveStagingIdentity identity) =>
            inner.VerifyOwnedStaging(identity);
        public void CreateDirectory(string physicalPath) => inner.CreateDirectory(physicalPath);
        public bool IsRealDirectory(string physicalPath) => inner.IsRealDirectory(physicalPath);
        public void VerifyTreeHasNoLinks(ArchiveStagingIdentity identity) =>
            inner.VerifyTreeHasNoLinks(identity);
        public Stream CreateFileNew(string physicalPath) => inner.CreateFileNew(physicalPath);
        public void TrySetLastWriteTimeUtc(string physicalPath, DateTimeOffset value) =>
            inner.TrySetLastWriteTimeUtc(physicalPath, value);
        public void MoveNew(string sourcePhysicalPath, string destinationPhysicalPath) =>
            inner.MoveNew(sourcePhysicalPath, destinationPhysicalPath);
        public void DeleteOwnedDirectoryTree(ArchiveStagingIdentity identity) =>
            inner.DeleteOwnedDirectoryTree(identity);
    }

    private sealed class FailingStagingCreationFileSystem(
        IArchiveExtractionRuntimeFileSystem inner) : IArchiveExtractionRuntimeFileSystem
    {
        public bool DirectoryExists(string physicalPath) => inner.DirectoryExists(physicalPath);
        public IReadOnlyList<ArchiveDestinationEntry> ListChildren(string physicalDirectory) =>
            inner.ListChildren(physicalDirectory);
        public long? GetAvailableFreeSpace(string physicalDirectory) =>
            inner.GetAvailableFreeSpace(physicalDirectory);
        public IDisposable OpenReadShared(string physicalPath) => inner.OpenReadShared(physicalPath);

        public ArchiveStagingIdentity CreateOwnedStagingDirectory(string physicalPath)
        {
            var identity = inner.CreateOwnedStagingDirectory(physicalPath);
            throw new ArchiveStagingCreationException(
                identity,
                new IOException("simulated marker failure"));
        }

        public bool VerifyOwnedStaging(ArchiveStagingIdentity identity) =>
            inner.VerifyOwnedStaging(identity);
        public void CreateDirectory(string physicalPath) => inner.CreateDirectory(physicalPath);
        public bool IsRealDirectory(string physicalPath) => inner.IsRealDirectory(physicalPath);
        public void VerifyTreeHasNoLinks(ArchiveStagingIdentity identity) =>
            inner.VerifyTreeHasNoLinks(identity);
        public Stream CreateFileNew(string physicalPath) => inner.CreateFileNew(physicalPath);
        public void TrySetLastWriteTimeUtc(string physicalPath, DateTimeOffset value) =>
            inner.TrySetLastWriteTimeUtc(physicalPath, value);
        public void MoveNew(string sourcePhysicalPath, string destinationPhysicalPath) =>
            inner.MoveNew(sourcePhysicalPath, destinationPhysicalPath);
        public void DeleteOwnedDirectoryTree(ArchiveStagingIdentity identity) =>
            inner.DeleteOwnedDirectoryTree(identity);
    }

    private sealed class GatedMoveFileSystem(
        IArchiveExtractionRuntimeFileSystem inner,
        MoveGate gate) : IArchiveExtractionRuntimeFileSystem
    {
        private int _moves;

        public bool DirectoryExists(string physicalPath) => inner.DirectoryExists(physicalPath);
        public IReadOnlyList<ArchiveDestinationEntry> ListChildren(string physicalDirectory) =>
            inner.ListChildren(physicalDirectory);
        public long? GetAvailableFreeSpace(string physicalDirectory) =>
            inner.GetAvailableFreeSpace(physicalDirectory);
        public IDisposable OpenReadShared(string physicalPath) => inner.OpenReadShared(physicalPath);
        public ArchiveStagingIdentity CreateOwnedStagingDirectory(string physicalPath) =>
            inner.CreateOwnedStagingDirectory(physicalPath);
        public bool VerifyOwnedStaging(ArchiveStagingIdentity identity) =>
            inner.VerifyOwnedStaging(identity);
        public void CreateDirectory(string physicalPath) => inner.CreateDirectory(physicalPath);
        public bool IsRealDirectory(string physicalPath) => inner.IsRealDirectory(physicalPath);
        public void VerifyTreeHasNoLinks(ArchiveStagingIdentity identity) =>
            inner.VerifyTreeHasNoLinks(identity);
        public Stream CreateFileNew(string physicalPath) => inner.CreateFileNew(physicalPath);
        public void TrySetLastWriteTimeUtc(string physicalPath, DateTimeOffset value) =>
            inner.TrySetLastWriteTimeUtc(physicalPath, value);
        public void DeleteOwnedDirectoryTree(ArchiveStagingIdentity identity) =>
            inner.DeleteOwnedDirectoryTree(identity);

        public void MoveNew(string sourcePhysicalPath, string destinationPhysicalPath)
        {
            if (Interlocked.Increment(ref _moves) == 1)
            {
                gate.Entered.TrySetResult();
                gate.Release.Wait();
            }

            inner.MoveNew(sourcePhysicalPath, destinationPhysicalPath);
        }
    }

    private sealed class FailingMoveFileSystem(
        IArchiveExtractionRuntimeFileSystem inner,
        MoveFailurePolicy policy) : IArchiveExtractionRuntimeFileSystem
    {
        private int _finalMoves;

        public bool DirectoryExists(string physicalPath) => inner.DirectoryExists(physicalPath);
        public IReadOnlyList<ArchiveDestinationEntry> ListChildren(string physicalDirectory) =>
            inner.ListChildren(physicalDirectory);
        public long? GetAvailableFreeSpace(string physicalDirectory) =>
            inner.GetAvailableFreeSpace(physicalDirectory);
        public IDisposable OpenReadShared(string physicalPath) => inner.OpenReadShared(physicalPath);
        public ArchiveStagingIdentity CreateOwnedStagingDirectory(string physicalPath) =>
            inner.CreateOwnedStagingDirectory(physicalPath);
        public bool VerifyOwnedStaging(ArchiveStagingIdentity identity) =>
            inner.VerifyOwnedStaging(identity);
        public void CreateDirectory(string physicalPath) => inner.CreateDirectory(physicalPath);
        public bool IsRealDirectory(string physicalPath) => inner.IsRealDirectory(physicalPath);
        public void VerifyTreeHasNoLinks(ArchiveStagingIdentity identity) =>
            inner.VerifyTreeHasNoLinks(identity);
        public Stream CreateFileNew(string physicalPath) => inner.CreateFileNew(physicalPath);
        public void TrySetLastWriteTimeUtc(string physicalPath, DateTimeOffset value) =>
            inner.TrySetLastWriteTimeUtc(physicalPath, value);
        public void DeleteOwnedDirectoryTree(ArchiveStagingIdentity identity) =>
            inner.DeleteOwnedDirectoryTree(identity);

        public void MoveNew(string sourcePhysicalPath, string destinationPhysicalPath)
        {
            var finalMove = sourcePhysicalPath.Contains(".partial", StringComparison.Ordinal) &&
                !destinationPhysicalPath.Contains(".partial", StringComparison.Ordinal);
            if (finalMove && policy.FailSecondFinalMove && Interlocked.Increment(ref _finalMoves) == 2)
            {
                throw new IOException("simulated final move failure");
            }

            if (!finalMove && policy.FailCompensation)
            {
                throw new IOException("simulated compensation failure");
            }

            inner.MoveNew(sourcePhysicalPath, destinationPhysicalPath);
        }
    }

    private sealed class SequentialOperationIdGenerator : IArchiveOperationIdGenerator
    {
        private int _next;

        public string CreateId() => $"operation-{Interlocked.Increment(ref _next)}";
    }
}

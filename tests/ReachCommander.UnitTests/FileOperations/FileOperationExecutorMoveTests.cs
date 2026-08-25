using ReachCommander.Application.FileOperations;
using ReachCommander.Application.Files;
using ReachCommander.Application.Sources;
using ReachCommander.Domain.Sources;
using ReachCommander.Infrastructure.FileOperations.Execution;
using ReachCommander.Infrastructure.FileOperations.Persistence;
using ReachCommander.Infrastructure.FileOperations.Planning;
using ReachCommander.Infrastructure.Mutations;
using ReachCommander.Infrastructure.Security;
using ReachCommander.UnitTests.Support;

namespace ReachCommander.UnitTests.FileOperations;

public sealed class FileOperationExecutorMoveTests : IDisposable
{
    private readonly TemporaryDirectory _temporary = new();
    private readonly string _sourceRoot;
    private readonly string _destinationRoot;
    private readonly FileOperationPlanner _planner;
    private readonly FileOperationRepository _repository;
    private readonly MoveTestFileSystem _fileSystem;
    private readonly FileOperationExecutor _executor;

    public FileOperationExecutorMoveTests()
    {
        _sourceRoot = _temporary.CreateDirectory("source");
        _destinationRoot = _temporary.CreateDirectory("destination");
        var sources = new FakeSourceCatalog(
            new("source", "Source", _sourceRoot, false, true, false),
            new("destination", "Destination", _destinationRoot, false, false, true));
        var pathSecurity = new PathSecurityService(sources);
        var inspector = new LocalFileOperationInspector(pathSecurity);
        var dataPaths = FileOperationDataPaths.FromAuthenticationRoot(
            _temporary.CreateDirectory("data"));
        _repository = new FileOperationRepository(dataPaths, TimeProvider.System);
        _planner = new FileOperationPlanner(
            sources,
            inspector,
            new JsonFileOperationPlanStore(dataPaths),
            TimeProvider.System);
        _fileSystem = new MoveTestFileSystem(
            new LocalFileOperationFileSystem(),
            _sourceRoot,
            _destinationRoot);
        _executor = new FileOperationExecutor(
            pathSecurity,
            inspector,
            _fileSystem,
            new DirectoryMutationLock(),
            _repository,
            TimeProvider.System);
    }

    [Fact]
    public void Cross_device_classifier_accepts_only_the_platform_native_code()
    {
        var expectedCode = OperatingSystem.IsWindows() ? 17 : 18;

        Assert.True(NativeMoveErrorClassifier.IsCrossDevice(
            new NativeIOException(unchecked((int)0x80070000) | expectedCode)));
        Assert.False(NativeMoveErrorClassifier.IsCrossDevice(
            new NativeIOException(unchecked((int)0x80070000) | 5)));
    }

    [Fact]
    public async Task Same_filesystem_move_renames_file_without_copying()
    {
        await File.WriteAllTextAsync(Path.Combine(_sourceRoot, "game.iso"), "bytes");

        var result = await ExecuteMoveAsync(["/game.iso"]);

        Assert.Equal(FileOperationPhase.Completed, result.Phase);
        Assert.False(File.Exists(Path.Combine(_sourceRoot, "game.iso")));
        Assert.Equal("bytes", await File.ReadAllTextAsync(Path.Combine(_destinationRoot, "game.iso")));
        Assert.Equal(0, _fileSystem.CopyCount);
    }

    [Fact]
    public async Task Cross_device_move_deletes_source_after_destination_commit()
    {
        await File.WriteAllTextAsync(Path.Combine(_sourceRoot, "game.iso"), "bytes");
        _fileSystem.ForceSourceMovesCrossDevice = true;

        var result = await ExecuteMoveAsync(["/game.iso"]);

        Assert.Equal(FileOperationPhase.Completed, result.Phase);
        Assert.False(File.Exists(Path.Combine(_sourceRoot, "game.iso")));
        Assert.Equal("bytes", await File.ReadAllTextAsync(Path.Combine(_destinationRoot, "game.iso")));
        Assert.Equal(["commit:/game.iso", "delete-source:/game.iso"], _fileSystem.MutationOrder);
    }

    [Fact]
    public async Task Source_delete_failure_reports_copied_but_not_removed()
    {
        await File.WriteAllTextAsync(Path.Combine(_sourceRoot, "game.iso"), "bytes");
        _fileSystem.ForceSourceMovesCrossDevice = true;
        _fileSystem.FailSourceDelete = true;

        var result = await ExecuteMoveAsync(["/game.iso"]);

        Assert.Equal(FileOperationPhase.CompletedWithErrors, result.Phase);
        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal(FileOperationItemResult.CopiedButNotRemoved, outcome.Result);
        Assert.Equal("move_source_not_removed", outcome.ErrorCode);
        Assert.True(File.Exists(Path.Combine(_sourceRoot, "game.iso")));
        Assert.True(File.Exists(Path.Combine(_destinationRoot, "game.iso")));
    }

    [Fact]
    public async Task Directory_move_merges_and_removes_empty_source_tree()
    {
        Directory.CreateDirectory(Path.Combine(_sourceRoot, "Games"));
        Directory.CreateDirectory(Path.Combine(_destinationRoot, "Games"));
        await File.WriteAllTextAsync(Path.Combine(_sourceRoot, "Games", "new.iso"), "new");
        await File.WriteAllTextAsync(Path.Combine(_destinationRoot, "Games", "keep.txt"), "keep");

        var result = await ExecuteMoveAsync(
            ["/Games"],
            FileOperationConflictDecision.Overwrite);

        Assert.Equal(FileOperationPhase.Completed, result.Phase);
        Assert.False(Directory.Exists(Path.Combine(_sourceRoot, "Games")));
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(_destinationRoot, "Games", "new.iso")));
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(_destinationRoot, "Games", "keep.txt")));
    }

    [Fact]
    public async Task Running_cancellation_repairs_current_item_and_preserves_source()
    {
        await File.WriteAllTextAsync(Path.Combine(_sourceRoot, "game.iso"), "bytes");
        _fileSystem.ForceSourceMovesCrossDevice = true;
        _fileSystem.PauseCopy = true;
        var execution = StartMoveAsync(["/game.iso"]);
        await _fileSystem.CopyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var operation = Assert.Single(await _repository.ListAsync(default));

        await _repository.RequestCancellationAsync(operation.OperationId, default);
        _fileSystem.ContinueCopy.TrySetResult();
        var result = await execution;

        Assert.Equal(FileOperationPhase.Cancelled, result.Phase);
        Assert.True(File.Exists(Path.Combine(_sourceRoot, "game.iso")));
        Assert.False(File.Exists(Path.Combine(_destinationRoot, "game.iso")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(
            _destinationRoot,
            ".reachcommander-operation-*",
            SearchOption.AllDirectories));
    }

    public void Dispose() => _temporary.Dispose();

    private async Task<FileOperationStatus> ExecuteMoveAsync(
        IReadOnlyList<string> paths,
        FileOperationConflictDecision decision = FileOperationConflictDecision.Overwrite) =>
        await StartMoveAsync(paths, decision);

    private async Task<FileOperationStatus> StartMoveAsync(
        IReadOnlyList<string> paths,
        FileOperationConflictDecision decision = FileOperationConflictDecision.Overwrite)
    {
        var preview = await _planner.PreviewAsync(
            new(FileOperationKind.Move, "source", paths, "destination", "/"),
            default);
        var plan = await _planner.GetValidatedPlanAsync(preview.PlanId, default);
        var resolutions = preview.Conflicts
            .Select(conflict => new FileOperationConflictResolution(conflict.ConflictId, decision))
            .ToArray();
        await _repository.EnqueueAsync(
            plan,
            new FileOperationSubmissionApproval(resolutions, false),
            default);
        var claimed = await _repository.TryTakeNextAsync(default);
        return await _executor.ExecuteAsync(claimed!, default);
    }

    private sealed class FakeSourceCatalog(params SourceDefinition[] sources) : ISourceCatalog
    {
        public ValueTask<IReadOnlyList<SourceDefinition>> GetDefinitionsAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<SourceDefinition>>(sources);

        public ValueTask<IReadOnlyList<SourceSnapshot>> GetSnapshotsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<SourceDefinition> GetRequiredAsync(string sourceId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(sources.Single(source => source.Id == sourceId));
    }

    private sealed class MoveTestFileSystem(
        IFileOperationFileSystem inner,
        string sourceRoot,
        string destinationRoot) : IFileOperationFileSystem
    {
        public bool ForceSourceMovesCrossDevice { get; set; }
        public bool FailSourceDelete { get; set; }
        public bool PauseCopy { get; set; }
        public int CopyCount { get; private set; }
        public List<string> MutationOrder { get; } = [];
        public TaskCompletionSource CopyStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ContinueCopy { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Exists(string physicalPath) => inner.Exists(physicalPath);
        public void CreateDirectory(string physicalPath) => inner.CreateDirectory(physicalPath);
        public long GetFileLength(string physicalPath) => inner.GetFileLength(physicalPath);

        public async Task<long> CopyFileAsync(
            string source,
            string destination,
            Func<long, CancellationToken, ValueTask> onBytes,
            CancellationToken cancellationToken)
        {
            CopyCount++;
            CopyStarted.TrySetResult();
            if (PauseCopy)
            {
                await ContinueCopy.Task.WaitAsync(cancellationToken);
            }

            return await inner.CopyFileAsync(source, destination, onBytes, cancellationToken);
        }

        public MoveAttempt TryMove(string source, string destination)
        {
            if (ForceSourceMovesCrossDevice && IsWithin(sourceRoot, source) &&
                !Path.GetFileName(source).StartsWith(".reachcommander-operation-", StringComparison.OrdinalIgnoreCase))
            {
                return MoveAttempt.CrossDevice;
            }

            var attempt = inner.TryMove(source, destination);
            if (IsWithin(destinationRoot, destination) &&
                Path.GetFileName(source).Contains("-stage-", StringComparison.Ordinal))
            {
                MutationOrder.Add($"commit:/{Path.GetRelativePath(destinationRoot, destination).Replace('\\', '/')}");
            }

            return attempt;
        }

        public void DeleteFile(string physicalPath)
        {
            if (IsWithin(sourceRoot, physicalPath))
            {
                if (FailSourceDelete)
                {
                    throw new UnauthorizedAccessException("Injected source delete failure.");
                }

                MutationOrder.Add($"delete-source:/{Path.GetRelativePath(sourceRoot, physicalPath).Replace('\\', '/')}");
            }

            inner.DeleteFile(physicalPath);
        }

        public void DeleteDirectory(string physicalPath, bool recursive) =>
            inner.DeleteDirectory(physicalPath, recursive);

        public void ApplyBasicMetadata(string source, string destination) =>
            inner.ApplyBasicMetadata(source, destination);

        public long? GetAvailableBytes(string physicalDirectory) =>
            inner.GetAvailableBytes(physicalDirectory);

        public FileAttributes GetAttributes(string physicalPath) =>
            inner.GetAttributes(physicalPath);

        private static bool IsWithin(string root, string path)
        {
            var relative = Path.GetRelativePath(root, path);
            return !Path.IsPathRooted(relative) && relative != ".." &&
                !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
        }
    }

    private sealed class NativeIOException : IOException
    {
        public NativeIOException(int hresult)
        {
            HResult = hresult;
        }
    }
}

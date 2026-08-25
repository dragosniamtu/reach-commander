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

public sealed class FileOperationExecutorCopyTests : IDisposable
{
    private readonly TemporaryDirectory _temporary = new();
    private readonly string _mediaRoot;
    private readonly string _downloadsRoot;
    private readonly FileOperationPlanner _planner;
    private readonly FileOperationRepository _repository;
    private readonly TestFileSystem _fileSystem;
    private readonly FileOperationExecutor _executor;
    private readonly DirectoryMutationLock _mutationLock = new();

    public FileOperationExecutorCopyTests()
    {
        _mediaRoot = _temporary.CreateDirectory("media");
        _downloadsRoot = _temporary.CreateDirectory("downloads");
        var sources = new FakeSourceCatalog(
            new("media", "Media", _mediaRoot, true, true, false),
            new("downloads", "Downloads", _downloadsRoot, false, false, true));
        var pathSecurity = new PathSecurityService(sources);
        var inspector = new LocalFileOperationInspector(pathSecurity);
        var dataPaths = FileOperationDataPaths.FromAuthenticationRoot(
            _temporary.CreateDirectory("data"));
        var plans = new JsonFileOperationPlanStore(dataPaths);
        _repository = new FileOperationRepository(dataPaths, TimeProvider.System);
        _planner = new FileOperationPlanner(sources, inspector, plans, TimeProvider.System);
        _fileSystem = new TestFileSystem(new LocalFileOperationFileSystem());
        _executor = new FileOperationExecutor(
            pathSecurity,
            inspector,
            _fileSystem,
            _mutationLock,
            _repository,
            TimeProvider.System);
    }

    [Fact]
    public async Task Copy_file_commits_from_staging_and_cleans_owned_names()
    {
        await File.WriteAllTextAsync(Path.Combine(_mediaRoot, "movie.mkv"), "new-content");

        var result = await ExecuteCopyAsync(["/movie.mkv"]);

        Assert.Equal(FileOperationPhase.Completed, result.Phase);
        Assert.Equal("new-content", await File.ReadAllTextAsync(Path.Combine(_downloadsRoot, "movie.mkv")));
        AssertNoOwnedEntries(_downloadsRoot);
        Assert.Equal(FileOperationItemResult.Completed, Assert.Single(result.Outcomes).Result);
    }

    [Fact]
    public async Task Overwrite_commit_failure_restores_quarantined_destination()
    {
        await File.WriteAllTextAsync(Path.Combine(_mediaRoot, "movie.mkv"), "new-content");
        await File.WriteAllTextAsync(Path.Combine(_downloadsRoot, "movie.mkv"), "old-content");
        _fileSystem.FailStagingCommitTo = Path.Combine(_downloadsRoot, "movie.mkv");

        var result = await ExecuteCopyAsync(
            ["/movie.mkv"],
            FileOperationConflictDecision.Overwrite);

        Assert.Equal(FileOperationPhase.CompletedWithErrors, result.Phase);
        Assert.Equal("old-content", await File.ReadAllTextAsync(Path.Combine(_downloadsRoot, "movie.mkv")));
        AssertNoOwnedEntries(_downloadsRoot);
        Assert.Equal(FileOperationItemResult.Failed, Assert.Single(result.Outcomes).Result);
    }

    [Fact]
    public async Task Directory_overwrite_merges_and_preserves_destination_only_entries()
    {
        Directory.CreateDirectory(Path.Combine(_mediaRoot, "Shows"));
        Directory.CreateDirectory(Path.Combine(_downloadsRoot, "Shows"));
        await File.WriteAllTextAsync(Path.Combine(_mediaRoot, "Shows", "Episode.mkv"), "episode");
        await File.WriteAllTextAsync(Path.Combine(_downloadsRoot, "Shows", "Episode.mkv"), "old");
        await File.WriteAllTextAsync(Path.Combine(_downloadsRoot, "Shows", "Poster.jpg"), "poster");

        var result = await ExecuteCopyAsync(
            ["/Shows"],
            FileOperationConflictDecision.Overwrite);

        Assert.Equal(FileOperationPhase.Completed, result.Phase);
        Assert.Equal("episode", await File.ReadAllTextAsync(Path.Combine(_downloadsRoot, "Shows", "Episode.mkv")));
        Assert.Equal("poster", await File.ReadAllTextAsync(Path.Combine(_downloadsRoot, "Shows", "Poster.jpg")));
        AssertNoOwnedEntries(_downloadsRoot);
    }

    [Fact]
    public async Task Create_unique_name_preserves_existing_file()
    {
        await File.WriteAllTextAsync(Path.Combine(_mediaRoot, "movie.txt"), "new");
        await File.WriteAllTextAsync(Path.Combine(_downloadsRoot, "movie.txt"), "old");

        var result = await ExecuteCopyAsync(
            ["/movie.txt"],
            FileOperationConflictDecision.CreateUniqueName);

        Assert.Equal(FileOperationPhase.Completed, result.Phase);
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(_downloadsRoot, "movie.txt")));
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(_downloadsRoot, "movie (2).txt")));
    }

    [Fact]
    public async Task New_destination_conflict_after_preview_fails_stale_without_overwrite()
    {
        await File.WriteAllTextAsync(Path.Combine(_mediaRoot, "movie.txt"), "source");
        var preview = await _planner.PreviewAsync(
            new(FileOperationKind.Copy, "media", ["/movie.txt"], "downloads", "/"),
            default);
        await File.WriteAllTextAsync(Path.Combine(_downloadsRoot, "movie.txt"), "late");

        var result = await ExecutePlanAsync(preview, []);

        Assert.Equal(FileOperationPhase.Failed, result.Phase);
        Assert.Contains(result.Warnings, warning => warning.Contains("changed", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("late", await File.ReadAllTextAsync(Path.Combine(_downloadsRoot, "movie.txt")));
    }

    [Fact]
    public async Task Executor_waits_for_overlapping_directory_mutation_lock()
    {
        await File.WriteAllTextAsync(Path.Combine(_mediaRoot, "movie.txt"), "source");
        var preview = await _planner.PreviewAsync(
            new(FileOperationKind.Copy, "media", ["/movie.txt"], "downloads", "/"),
            default);
        var held = await _mutationLock.AcquireAsync(
            "downloads",
            "/",
            default);

        var execution = ExecutePlanAsync(preview, []);
        await Task.Delay(50);
        Assert.False(execution.IsCompleted);
        await held.DisposeAsync();
        var result = await execution;
        Assert.Equal(FileOperationPhase.Completed, result.Phase);
    }

    public void Dispose() => _temporary.Dispose();

    private async Task<FileOperationStatus> ExecuteCopyAsync(
        IReadOnlyList<string> paths,
        FileOperationConflictDecision decision = FileOperationConflictDecision.Overwrite)
    {
        var preview = await _planner.PreviewAsync(
            new(FileOperationKind.Copy, "media", paths, "downloads", "/"),
            default);
        var resolutions = preview.Conflicts
            .Select(conflict => new FileOperationConflictResolution(conflict.ConflictId, decision))
            .ToArray();
        return await ExecutePlanAsync(preview, resolutions);
    }

    private async Task<FileOperationStatus> ExecutePlanAsync(
        FileOperationPreview preview,
        IReadOnlyList<FileOperationConflictResolution> resolutions)
    {
        var plan = await _planner.GetValidatedPlanAsync(preview.PlanId, default);
        var queued = await _repository.EnqueueAsync(
            plan,
            new FileOperationSubmissionApproval(resolutions, false),
            default);
        var claimed = await _repository.TryTakeNextAsync(default);
        Assert.Equal(queued.OperationId, claimed!.OperationId);
        return await _executor.ExecuteAsync(claimed, default);
    }

    private static void AssertNoOwnedEntries(string root) => Assert.Empty(
        Directory.EnumerateFileSystemEntries(root, ".reachcommander-operation-*", SearchOption.AllDirectories));

    private sealed class FakeSourceCatalog(params SourceDefinition[] sources) : ISourceCatalog
    {
        public ValueTask<IReadOnlyList<SourceDefinition>> GetDefinitionsAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<SourceDefinition>>(sources);

        public ValueTask<IReadOnlyList<SourceSnapshot>> GetSnapshotsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<SourceDefinition> GetRequiredAsync(string sourceId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(sources.Single(source => source.Id == sourceId));
    }

    private sealed class TestFileSystem(IFileOperationFileSystem inner) : IFileOperationFileSystem
    {
        public string? FailStagingCommitTo { get; set; }

        public bool Exists(string physicalPath) => inner.Exists(physicalPath);
        public void CreateDirectory(string physicalPath) => inner.CreateDirectory(physicalPath);
        public long GetFileLength(string physicalPath) => inner.GetFileLength(physicalPath);
        public Task<long> CopyFileAsync(string source, string destination, Func<long, CancellationToken, ValueTask> onBytes, CancellationToken cancellationToken) =>
            inner.CopyFileAsync(source, destination, onBytes, cancellationToken);
        public MoveAttempt TryMove(string source, string destination)
        {
            if (FailStagingCommitTo is not null &&
                Path.GetFileName(source).Contains("-stage-", StringComparison.Ordinal) &&
                Path.GetFullPath(destination).Equals(Path.GetFullPath(FailStagingCommitTo), StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Injected staging commit failure.");
            }

            return inner.TryMove(source, destination);
        }
        public void DeleteFile(string physicalPath) => inner.DeleteFile(physicalPath);
        public void DeleteDirectory(string physicalPath, bool recursive) => inner.DeleteDirectory(physicalPath, recursive);
        public void ApplyBasicMetadata(string source, string destination) => inner.ApplyBasicMetadata(source, destination);
        public long? GetAvailableBytes(string physicalDirectory) => inner.GetAvailableBytes(physicalDirectory);
    }
}

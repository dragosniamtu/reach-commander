using ReachCommander.Application.FileOperations;
using ReachCommander.Application.Files;
using ReachCommander.Application.Sources;
using ReachCommander.Application.Trash;
using ReachCommander.Domain.Sources;
using ReachCommander.Infrastructure.FileOperations.Execution;
using ReachCommander.Infrastructure.FileOperations.Persistence;
using ReachCommander.Infrastructure.FileOperations.Planning;
using ReachCommander.Infrastructure.Mutations;
using ReachCommander.Infrastructure.Security;
using ReachCommander.Infrastructure.Trash;
using ReachCommander.UnitTests.Support;

namespace ReachCommander.UnitTests.Trash;

public sealed class TrashServiceTests : IDisposable
{
    private readonly TemporaryDirectory _temporary = new();
    private readonly string _mediaRoot;
    private readonly string _downloadsRoot;
    private readonly string _readOnlyRoot;
    private readonly TrashService _service;
    private readonly TrashManifestStore _manifestStore;
    private readonly FileOperationRepository _repository;
    private readonly TrashOperationExecutor _executor;

    public TrashServiceTests()
    {
        _mediaRoot = _temporary.CreateDirectory("media");
        _downloadsRoot = _temporary.CreateDirectory("downloads");
        _readOnlyRoot = _temporary.CreateDirectory("readonly");
        var sources = new FakeSourceCatalog(
            new("media", "Media", _mediaRoot, false, true, false),
            new("downloads", "Downloads", _downloadsRoot, false, false, true),
            new("readonly", "Read only", _readOnlyRoot, true, false, false));
        var security = new PathSecurityService(sources);
        var inspector = new LocalFileOperationInspector(security);
        var paths = FileOperationDataPaths.FromAuthenticationRoot(
            _temporary.CreateDirectory("data"));
        _repository = new FileOperationRepository(paths, TimeProvider.System);
        _manifestStore = new TrashManifestStore(sources, security);
        _service = new TrashService(
            sources,
            inspector,
            _manifestStore,
            new JsonFileOperationPlanStore(paths),
            _repository,
            new FileOperationQueue(),
            TimeProvider.System);
        _executor = new TrashOperationExecutor(
            security,
            inspector,
            new LocalFileOperationFileSystem(),
            new DirectoryMutationLock(),
            _manifestStore,
            _repository,
            TimeProvider.System);
    }

    [Fact]
    public async Task Trash_unavailable_does_not_fall_back_to_permanent_delete()
    {
        await File.WriteAllTextAsync(Path.Combine(_mediaRoot, "photo.jpg"), "photo");
        Directory.CreateDirectory(Path.Combine(_mediaRoot, TrashLayout.Root));
        await File.WriteAllTextAsync(
            Path.Combine(_mediaRoot, TrashLayout.Root, "unknown.txt"),
            "keep");

        var preview = await _service.PreviewDeleteAsync(
            new("media", ["/photo.jpg"], DeleteMode.Trash),
            default);

        Assert.False(preview.TrashAvailable);
        await Assert.ThrowsAsync<TrashUnavailableException>(() =>
            _service.SubmitDeleteAsync(new(preview.PlanId, false), default));
        Assert.True(File.Exists(Path.Combine(_mediaRoot, "photo.jpg")));
    }

    [Fact]
    public async Task Trash_commit_is_listed_and_removes_only_selected_item()
    {
        await File.WriteAllTextAsync(Path.Combine(_mediaRoot, "photo.jpg"), "photo");
        await File.WriteAllTextAsync(Path.Combine(_mediaRoot, "keep.jpg"), "keep");

        var entry = await TrashAsync("media", "/photo.jpg");

        Assert.False(File.Exists(Path.Combine(_mediaRoot, "photo.jpg")));
        Assert.True(File.Exists(Path.Combine(_mediaRoot, "keep.jpg")));
        Assert.Equal("/photo.jpg", entry.OriginalLogicalPath);
        Assert.Single(await _service.ListAsync("media", default));
    }

    [Fact]
    public async Task Restore_unique_commits_then_removes_manifest()
    {
        await File.WriteAllTextAsync(Path.Combine(_mediaRoot, "photo.jpg"), "trashed");
        var trashed = await TrashAsync("media", "/photo.jpg");
        await File.WriteAllTextAsync(Path.Combine(_mediaRoot, "photo.jpg"), "existing");
        var preview = await _service.PreviewRestoreAsync(new([trashed.TrashId]), default);
        var conflict = Assert.Single(preview.Conflicts);

        await _service.SubmitRestoreAsync(
            new(
                preview.PlanId,
                [new(conflict.ConflictId, FileOperationConflictDecision.CreateUniqueName)]),
            default);
        var result = await ExecuteNextAsync();

        Assert.Equal(FileOperationPhase.Completed, result.Phase);
        Assert.Equal("existing", await File.ReadAllTextAsync(Path.Combine(_mediaRoot, "photo.jpg")));
        Assert.Equal("trashed", await File.ReadAllTextAsync(Path.Combine(_mediaRoot, "photo (2).jpg")));
        Assert.Empty(await _service.ListAsync("media", default));
    }

    [Fact]
    public async Task Permanent_delete_requires_bound_confirmation()
    {
        await File.WriteAllTextAsync(Path.Combine(_mediaRoot, "photo.jpg"), "photo");
        var preview = await _service.PreviewDeleteAsync(
            new("media", ["/photo.jpg"], DeleteMode.Permanent),
            default);

        var exception = await Assert.ThrowsAsync<PermanentDeleteConfirmationRequiredException>(() =>
            _service.SubmitDeleteAsync(new(preview.PlanId, false), default));

        Assert.Equal(PermanentDeleteConfirmation.Warning, exception.PublicDetail);
        Assert.True(File.Exists(Path.Combine(_mediaRoot, "photo.jpg")));
        await _service.SubmitDeleteAsync(new(preview.PlanId, true), default);
        Assert.Equal(FileOperationPhase.Completed, (await ExecuteNextAsync()).Phase);
        Assert.False(File.Exists(Path.Combine(_mediaRoot, "photo.jpg")));
    }

    [Fact]
    public async Task Empty_trash_can_be_scoped_to_one_source()
    {
        await File.WriteAllTextAsync(Path.Combine(_mediaRoot, "media.jpg"), "media");
        await File.WriteAllTextAsync(Path.Combine(_downloadsRoot, "download.jpg"), "download");
        await TrashAsync("media", "/media.jpg");
        await TrashAsync("downloads", "/download.jpg");

        await _service.EmptyAsync(new("media", true), default);
        Assert.Equal(FileOperationPhase.Completed, (await ExecuteNextAsync()).Phase);

        Assert.Empty(await _service.ListAsync("media", default));
        Assert.Single(await _service.ListAsync("downloads", default));
    }

    [Fact]
    public async Task Restore_skip_keeps_existing_destination_and_trash_record()
    {
        await File.WriteAllTextAsync(Path.Combine(_mediaRoot, "photo.jpg"), "trashed");
        var trashed = await TrashAsync("media", "/photo.jpg");
        await File.WriteAllTextAsync(Path.Combine(_mediaRoot, "photo.jpg"), "existing");
        var preview = await _service.PreviewRestoreAsync(new([trashed.TrashId]), default);

        await _service.SubmitRestoreAsync(
            new(
                preview.PlanId,
                [new(preview.Conflicts.Single().ConflictId, FileOperationConflictDecision.Skip)]),
            default);
        Assert.Equal(FileOperationPhase.Completed, (await ExecuteNextAsync()).Phase);

        Assert.Equal("existing", await File.ReadAllTextAsync(Path.Combine(_mediaRoot, "photo.jpg")));
        Assert.Single(await _service.ListAsync("media", default));
    }

    [Fact]
    public async Task Restore_overwrite_replaces_destination_and_removes_record()
    {
        await File.WriteAllTextAsync(Path.Combine(_mediaRoot, "photo.jpg"), "trashed");
        var trashed = await TrashAsync("media", "/photo.jpg");
        await File.WriteAllTextAsync(Path.Combine(_mediaRoot, "photo.jpg"), "existing");
        var preview = await _service.PreviewRestoreAsync(new([trashed.TrashId]), default);

        await _service.SubmitRestoreAsync(
            new(
                preview.PlanId,
                [new(preview.Conflicts.Single().ConflictId, FileOperationConflictDecision.Overwrite)]),
            default);
        Assert.Equal(FileOperationPhase.Completed, (await ExecuteNextAsync()).Phase);

        Assert.Equal("trashed", await File.ReadAllTextAsync(Path.Combine(_mediaRoot, "photo.jpg")));
        Assert.Empty(await _service.ListAsync("media", default));
    }

    [Fact]
    public async Task Restore_preview_reports_and_execution_creates_missing_parent()
    {
        Directory.CreateDirectory(Path.Combine(_mediaRoot, "Album"));
        await File.WriteAllTextAsync(Path.Combine(_mediaRoot, "Album", "photo.jpg"), "photo");
        var trashed = await TrashAsync("media", "/Album/photo.jpg");
        Directory.Delete(Path.Combine(_mediaRoot, "Album"));

        var preview = await _service.PreviewRestoreAsync(new([trashed.TrashId]), default);
        Assert.Equal(["/Album"], preview.ParentsToCreate);
        await _service.SubmitRestoreAsync(new(preview.PlanId, []), default);
        Assert.Equal(FileOperationPhase.Completed, (await ExecuteNextAsync()).Phase);

        Assert.Equal("photo", await File.ReadAllTextAsync(Path.Combine(_mediaRoot, "Album", "photo.jpg")));
    }

    [Fact]
    public async Task Permanently_deleting_trash_item_requires_confirmation_and_preserves_canary()
    {
        await File.WriteAllTextAsync(Path.Combine(_mediaRoot, "photo.jpg"), "photo");
        await File.WriteAllTextAsync(Path.Combine(_mediaRoot, "keep.jpg"), "keep");
        var trashed = await TrashAsync("media", "/photo.jpg");

        await Assert.ThrowsAsync<PermanentDeleteConfirmationRequiredException>(() =>
            _service.PermanentlyDeleteAsync(new([trashed.TrashId], false), default));
        await _service.PermanentlyDeleteAsync(new([trashed.TrashId], true), default);
        Assert.Equal(FileOperationPhase.Completed, (await ExecuteNextAsync()).Phase);

        Assert.Empty(await _service.ListAsync("media", default));
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(_mediaRoot, "keep.jpg")));
    }

    [Fact]
    public async Task Delete_preview_rejects_read_only_source()
    {
        await File.WriteAllTextAsync(Path.Combine(_readOnlyRoot, "photo.jpg"), "photo");

        await Assert.ThrowsAsync<OperationSourceReadOnlyException>(() =>
            _service.PreviewDeleteAsync(
                new("readonly", ["/photo.jpg"], DeleteMode.Trash),
                default));
    }

    public void Dispose() => _temporary.Dispose();

    private async Task<TrashEntry> TrashAsync(string sourceId, string logicalPath)
    {
        var preview = await _service.PreviewDeleteAsync(
            new(sourceId, [logicalPath], DeleteMode.Trash),
            default);
        await _service.SubmitDeleteAsync(new(preview.PlanId, false), default);
        var result = await ExecuteNextAsync();
        Assert.Equal(FileOperationPhase.Completed, result.Phase);
        return Assert.Single(await _service.ListAsync(sourceId, default),
            entry => entry.OriginalLogicalPath == logicalPath);
    }

    private async Task<FileOperationStatus> ExecuteNextAsync()
    {
        var claimed = await _repository.TryTakeNextAsync(default);
        Assert.NotNull(claimed);
        return await _executor.ExecuteAsync(claimed, default);
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
}

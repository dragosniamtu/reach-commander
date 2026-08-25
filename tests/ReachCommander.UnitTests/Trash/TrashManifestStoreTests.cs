using ReachCommander.Application.Files;
using ReachCommander.Application.Sources;
using ReachCommander.Domain.Files;
using ReachCommander.Domain.Sources;
using ReachCommander.Infrastructure.FileOperations.Planning;
using ReachCommander.Infrastructure.Security;
using ReachCommander.Infrastructure.Trash;
using ReachCommander.UnitTests.Support;

namespace ReachCommander.UnitTests.Trash;

public sealed class TrashManifestStoreTests : IDisposable
{
    private readonly TemporaryDirectory _temporary = new();
    private readonly string _root;
    private readonly TrashManifestStore _store;

    public TrashManifestStoreTests()
    {
        _root = _temporary.CreateDirectory("media");
        var sources = new FakeSourceCatalog(
            new SourceDefinition("media", "Media", _root, false, true, false));
        _store = new TrashManifestStore(sources, new PathSecurityService(sources));
    }

    [Fact]
    public async Task Unknown_reserved_root_collision_is_unavailable_and_untouched()
    {
        var collision = Path.Combine(_root, TrashLayout.Root);
        Directory.CreateDirectory(collision);
        await File.WriteAllTextAsync(Path.Combine(collision, "unknown.txt"), "keep");

        var capability = await _store.GetCapabilityAsync("media", default);

        Assert.False(capability.IsAvailable);
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(collision, "unknown.txt")));
    }

    [Fact]
    public async Task Round_trip_manifest_is_strict_and_contains_logical_data_only()
    {
        var trashId = Guid.NewGuid();
        var paths = await _store.GetOrCreatePathsAsync("media", trashId, default);
        Directory.CreateDirectory(paths.ItemContainerPhysicalPath);
        await File.WriteAllTextAsync(paths.ItemPhysicalPath, "photo");
        var manifest = Manifest(trashId, DateTimeOffset.UtcNow, paths.ItemPhysicalPath);

        await _store.WriteManifestAsync(manifest, default);
        var record = Assert.Single(await _store.LoadValidAsync("media", default));
        var json = await File.ReadAllTextAsync(paths.ManifestPhysicalPath);

        Assert.Equal(manifest, record.Manifest);
        Assert.DoesNotContain(_root, json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("items/", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_manifest_is_isolated_and_valid_records_are_newest_first()
    {
        var older = Guid.NewGuid();
        var newer = Guid.NewGuid();
        await AddValidAsync(older, DateTimeOffset.UtcNow.AddMinutes(-1));
        await AddValidAsync(newer, DateTimeOffset.UtcNow);
        var layout = await _store.GetOrCreatePathsAsync("media", Guid.NewGuid(), default);
        await File.WriteAllTextAsync(
            Path.Combine(Path.GetDirectoryName(layout.ManifestPhysicalPath)!, $"{Guid.NewGuid():N}.json"),
            "{\"schemaVersion\":99}");

        var records = await _store.LoadValidAsync("media", default);

        Assert.Equal([newer, older], records.Select(record => record.Manifest.TrashId));
    }

    public void Dispose() => _temporary.Dispose();

    private async Task AddValidAsync(Guid trashId, DateTimeOffset deletedAt)
    {
        var paths = await _store.GetOrCreatePathsAsync("media", trashId, default);
        Directory.CreateDirectory(paths.ItemContainerPhysicalPath);
        await File.WriteAllTextAsync(paths.ItemPhysicalPath, "photo");
        await _store.WriteManifestAsync(
            Manifest(trashId, deletedAt, paths.ItemPhysicalPath),
            default);
    }

    private static TrashManifest Manifest(
        Guid trashId,
        DateTimeOffset deletedAt,
        string itemPath)
    {
        var info = new FileInfo(itemPath);
        info.Refresh();
        return new(
        TrashManifest.CurrentSchemaVersion,
        trashId,
        "media",
        "/photo.jpg",
        "photo.jpg",
        FileEntryType.File,
        5,
        deletedAt,
        $"items/{trashId:N}/item",
        new(
            FileEntryType.File,
            5,
            new DateTimeOffset(info.LastWriteTimeUtc),
            info.Attributes,
            false));
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

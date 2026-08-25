using ReachCommander.Application.Sources;
using ReachCommander.Domain.Files;
using ReachCommander.Domain.Sources;
using ReachCommander.Infrastructure.FileOperations.Planning;
using ReachCommander.Infrastructure.Security;
using ReachCommander.UnitTests.Support;

namespace ReachCommander.UnitTests.FileOperations;

public sealed class LocalFileOperationInspectorTests : IDisposable
{
    private readonly TemporaryDirectory _temporary = new();
    private readonly string _root;
    private readonly LocalFileOperationInspector _inspector;

    public LocalFileOperationInspectorTests()
    {
        _root = _temporary.CreateDirectory("media");
        var source = new SourceDefinition("media", "Media", _root, false, true, false);
        _inspector = new LocalFileOperationInspector(
            new PathSecurityService(new FakeSourceCatalog(source)));
    }

    [Fact]
    public async Task GetRequiredAsync_returns_stable_file_fingerprint()
    {
        var physicalPath = Path.Combine(_root, "movie.mkv");
        await File.WriteAllTextAsync(physicalPath, "content");
        File.SetLastWriteTimeUtc(physicalPath, DateTime.Parse("2026-08-25T10:00:00Z").ToUniversalTime());

        var entry = await _inspector.GetRequiredAsync("media", "/movie.mkv", default);

        Assert.Equal(FileEntryType.File, entry.Type);
        Assert.Equal(7, entry.Length);
        Assert.Equal("movie.mkv", entry.Name);
        Assert.False(entry.IsSymbolicLink);
        Assert.Equal(File.GetLastWriteTimeUtc(physicalPath), entry.Fingerprint.ModifiedAt.UtcDateTime);
    }

    [Fact]
    public async Task ListChildrenAsync_is_sorted_and_hides_internal_names()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "b.txt"), "b");
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "a");
        Directory.CreateDirectory(Path.Combine(_root, ".reachcommander-trash"));

        var entries = await _inspector.ListChildrenAsync("media", "/", default);

        Assert.Equal(["a.txt", "b.txt"], entries.Select(entry => entry.Name));
    }

    [Fact]
    public async Task TryGetAsync_returns_null_for_missing_entry()
    {
        var entry = await _inspector.TryGetAsync("media", "/missing.txt", default);

        Assert.Null(entry);
    }

    public void Dispose() => _temporary.Dispose();

    private sealed class FakeSourceCatalog(SourceDefinition source) : ISourceCatalog
    {
        public ValueTask<IReadOnlyList<SourceDefinition>> GetDefinitionsAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<SourceDefinition>>([source]);

        public ValueTask<IReadOnlyList<SourceSnapshot>> GetSnapshotsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<SourceDefinition> GetRequiredAsync(string sourceId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(source);
    }
}

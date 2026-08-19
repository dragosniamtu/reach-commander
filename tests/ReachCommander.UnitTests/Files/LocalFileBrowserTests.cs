using ReachCommander.Application.Files;
using ReachCommander.Application.Sources;
using ReachCommander.Domain.Files;
using ReachCommander.Domain.Sources;
using ReachCommander.Infrastructure.FileSystem;
using ReachCommander.Infrastructure.Security;
using ReachCommander.UnitTests.Support;

namespace ReachCommander.UnitTests.Files;

public sealed class LocalFileBrowserTests : IDisposable
{
    private readonly TemporaryDirectory _temporary = new();
    private readonly string _sourceRoot;
    private readonly LocalFileBrowser _browser;

    public LocalFileBrowserTests()
    {
        _sourceRoot = _temporary.CreateDirectory("downloads");
        var source = new SourceDefinition(
            "downloads",
            "Downloads",
            _sourceRoot,
            IsReadOnly: false,
            DefaultLeft: true,
            DefaultRight: false);
        var pathSecurity = new PathSecurityService(new FakeSourceCatalog(source));
        _browser = new LocalFileBrowser(pathSecurity);
    }

    [Fact]
    public async Task ListAsync_returns_logical_file_and_directory_metadata()
    {
        var complete = Directory.CreateDirectory(System.IO.Path.Combine(_sourceRoot, "Complete"));
        File.WriteAllText(System.IO.Path.Combine(_sourceRoot, "movie.mkv"), "1234567");
        File.WriteAllText(System.IO.Path.Combine(_sourceRoot, ".hidden"), "x");
        complete.LastWriteTimeUtc = new DateTime(2026, 8, 18, 12, 30, 0, DateTimeKind.Utc);

        var entries = await _browser.ListAsync("downloads", "/", CancellationToken.None);

        var directory = Assert.Single(entries, entry => entry.Name == "Complete");
        Assert.Equal("/Complete", directory.RelativePath);
        Assert.Equal(FileEntryType.Directory, directory.Type);
        Assert.Null(directory.Size);
        Assert.Null(directory.Extension);

        var movie = Assert.Single(entries, entry => entry.Name == "movie.mkv");
        Assert.Equal("/movie.mkv", movie.RelativePath);
        Assert.Equal(FileEntryType.File, movie.Type);
        Assert.Equal(7, movie.Size);
        Assert.Equal("mkv", movie.Extension);
        Assert.DoesNotContain(_sourceRoot, movie.RelativePath, StringComparison.OrdinalIgnoreCase);

        var hidden = Assert.Single(entries, entry => entry.Name == ".hidden");
        Assert.Null(hidden.Extension);
    }

    [Fact]
    public async Task ListAsync_marks_read_only_files()
    {
        var path = System.IO.Path.Combine(_sourceRoot, "locked.txt");
        File.WriteAllText(path, "locked");
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);

        try
        {
            var entries = await _browser.ListAsync("downloads", "/", CancellationToken.None);

            var locked = Assert.Single(entries);
            Assert.True(locked.IsReadOnly);
            Assert.Contains(nameof(FileAttributes.ReadOnly), locked.Attributes, StringComparison.Ordinal);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task GetInfoAsync_returns_metadata_for_one_entry()
    {
        var directory = _temporary.CreateDirectory("downloads/Complete");
        var path = System.IO.Path.Combine(directory, "notes.txt");
        await File.WriteAllTextAsync(path, "notes");

        var entry = await _browser.GetInfoAsync(
            "downloads",
            "/Complete/notes.txt",
            CancellationToken.None);

        Assert.Equal("notes.txt", entry.Name);
        Assert.Equal("/Complete/notes.txt", entry.RelativePath);
        Assert.Equal(FileEntryType.File, entry.Type);
        Assert.Equal(5, entry.Size);
        Assert.Equal("txt", entry.Extension);
    }

    [Fact]
    public async Task ListAsync_rejects_a_file_as_a_directory()
    {
        await File.WriteAllTextAsync(System.IO.Path.Combine(_sourceRoot, "notes.txt"), "notes");

        await Assert.ThrowsAsync<InvalidLogicalPathException>(
            () => _browser.ListAsync("downloads", "/notes.txt", CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task ListAsync_honors_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _browser.ListAsync("downloads", "/", cancellation.Token).AsTask());
    }

    public void Dispose() => _temporary.Dispose();

    private sealed class FakeSourceCatalog(SourceDefinition source) : ISourceCatalog
    {
        public ValueTask<IReadOnlyList<SourceDefinition>> GetDefinitionsAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<SourceDefinition>>([source]);

        public ValueTask<IReadOnlyList<SourceSnapshot>> GetSnapshotsAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<SourceDefinition> GetRequiredAsync(
            string sourceId,
            CancellationToken cancellationToken) => ValueTask.FromResult(source);
    }
}

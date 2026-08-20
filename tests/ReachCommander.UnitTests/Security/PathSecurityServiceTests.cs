using ReachCommander.Application.Files;
using ReachCommander.Application.Sources;
using ReachCommander.Domain.Sources;
using ReachCommander.Infrastructure.Security;
using ReachCommander.UnitTests.Support;

namespace ReachCommander.UnitTests.Security;

public sealed class PathSecurityServiceTests : IDisposable
{
    private readonly TemporaryDirectory _temporary = new();
    private readonly string _sourceRoot;
    private readonly PathSecurityService _service;

    public PathSecurityServiceTests()
    {
        _sourceRoot = _temporary.CreateDirectory("media");
        Directory.CreateDirectory(System.IO.Path.Combine(_sourceRoot, "Movies", "Sci-Fi"));
        _service = CreateService(_sourceRoot);
    }

    [Theory]
    [InlineData("/", "/")]
    [InlineData("/Movies//./Sci-Fi/", "/Movies/Sci-Fi")]
    [InlineData("/Movies\\Sci-Fi", "/Movies/Sci-Fi")]
    public async Task ResolveAsync_normalizes_safe_logical_paths(
        string input,
        string expectedLogicalPath)
    {
        var resolved = await _service.ResolveAsync("media", input, CancellationToken.None);

        Assert.Equal(expectedLogicalPath, resolved.LogicalPath);
        Assert.True(System.IO.Path.IsPathFullyQualified(resolved.PhysicalPath));
        Assert.True(IsWithin(_sourceRoot, resolved.PhysicalPath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Movies")]
    [InlineData("/../secret")]
    [InlineData("/Movies/../../secret")]
    [InlineData("C:/Windows/System32")]
    [InlineData("C:\\Windows\\System32")]
    [InlineData("//server/share")]
    [InlineData("\\\\server\\share")]
    public async Task ResolveAsync_rejects_physical_or_traversing_paths(string logicalPath)
    {
        await Assert.ThrowsAsync<InvalidLogicalPathException>(
            () => _service.ResolveAsync("media", logicalPath, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task ResolveAsync_rejects_null_bytes()
    {
        await Assert.ThrowsAsync<InvalidLogicalPathException>(
            () => _service.ResolveAsync("media", "/Movies\0secret", CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task ResolveAsync_reports_a_missing_entry()
    {
        await Assert.ThrowsAsync<EntryNotFoundException>(
            () => _service.ResolveAsync("media", "/Missing", CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task ResolveAsync_reports_an_unavailable_source_root()
    {
        var missingRoot = System.IO.Path.Combine(_temporary.Path, "not-mounted");
        var service = CreateService(missingRoot);

        await Assert.ThrowsAsync<SourceUnavailableException>(
            () => service.ResolveAsync("media", "/", CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task ResolveAsync_honors_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.ResolveAsync("media", "/Movies", cancellation.Token).AsTask());
    }

    [Fact]
    public async Task ResolveAsync_allows_a_symlink_whose_target_is_inside_the_source()
    {
        var target = Directory.CreateDirectory(System.IO.Path.Combine(_sourceRoot, "Movies"));
        var link = System.IO.Path.Combine(_sourceRoot, "safe-link");
        if (!TryCreateDirectoryLink(link, target.FullName))
        {
            return;
        }

        var resolved = await _service.ResolveAsync("media", "/safe-link/Sci-Fi", CancellationToken.None);

        Assert.Equal("/safe-link/Sci-Fi", resolved.LogicalPath);
        Assert.True(IsWithin(_sourceRoot, resolved.PhysicalPath));
    }

    [Fact]
    public async Task ResolveAsync_rejects_a_symlink_whose_target_escapes_the_source()
    {
        var outside = _temporary.CreateDirectory("outside");
        Directory.CreateDirectory(System.IO.Path.Combine(outside, "private"));
        var link = System.IO.Path.Combine(_sourceRoot, "escape-link");
        if (!TryCreateDirectoryLink(link, outside))
        {
            return;
        }

        await Assert.ThrowsAsync<PathConfinementException>(
            () => _service.ResolveAsync("media", "/escape-link/private", CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task ResolveChildAsync_returns_a_confined_path_for_a_missing_child()
    {
        var resolved = await _service.ResolveChildAsync(
            "media",
            "/Movies",
            "renamed.mkv",
            CancellationToken.None);

        Assert.Equal("/Movies/renamed.mkv", resolved.LogicalPath);
        Assert.Equal(
            System.IO.Path.Combine(_sourceRoot, "Movies", "renamed.mkv"),
            resolved.PhysicalPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../escape")]
    [InlineData("sub/name")]
    [InlineData("sub\\name")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("C:drive")]
    [InlineData("nul\0name")]
    public async Task ResolveChildAsync_rejects_non_component_names(string childName)
    {
        await Assert.ThrowsAsync<InvalidLogicalPathException>(() =>
            _service.ResolveChildAsync(
                "media",
                "/Movies",
                childName,
                CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task ResolveChildAsync_does_not_follow_the_final_child_link()
    {
        var outside = _temporary.CreateDirectory("child-link-outside");
        var link = System.IO.Path.Combine(_sourceRoot, "Movies", "linked");
        if (!TryCreateDirectoryLink(link, outside))
        {
            return;
        }

        var resolved = await _service.ResolveChildAsync(
            "media",
            "/Movies",
            "linked",
            CancellationToken.None);

        Assert.Equal(System.IO.Path.GetFullPath(link), resolved.PhysicalPath);
    }

    [Fact]
    public async Task ResolveChildAsync_rejects_a_parent_that_is_not_a_directory()
    {
        File.WriteAllText(System.IO.Path.Combine(_sourceRoot, "file.txt"), "file");

        await Assert.ThrowsAsync<InvalidLogicalPathException>(() =>
            _service.ResolveChildAsync(
                "media",
                "/file.txt",
                "child",
                CancellationToken.None).AsTask());
    }

    public void Dispose() => _temporary.Dispose();

    private static PathSecurityService CreateService(string sourceRoot)
    {
        var source = new SourceDefinition(
            "media",
            "Media",
            sourceRoot,
            IsReadOnly: false,
            DefaultLeft: true,
            DefaultRight: false);
        return new PathSecurityService(new FakeSourceCatalog(source));
    }

    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool IsWithin(string root, string candidate)
    {
        var relative = System.IO.Path.GetRelativePath(root, candidate);
        return relative.Length == 0 ||
            (!System.IO.Path.IsPathRooted(relative) &&
             relative != ".." &&
             !relative.StartsWith($"..{System.IO.Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private sealed class FakeSourceCatalog(SourceDefinition source) : ISourceCatalog
    {
        public ValueTask<IReadOnlyList<SourceDefinition>> GetDefinitionsAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<SourceDefinition>>([source]);

        public ValueTask<IReadOnlyList<SourceSnapshot>> GetSnapshotsAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<SourceDefinition> GetRequiredAsync(
            string sourceId,
            CancellationToken cancellationToken) =>
            sourceId == source.Id
                ? ValueTask.FromResult(source)
                : ValueTask.FromException<SourceDefinition>(new SourceNotFoundException(sourceId));
    }
}

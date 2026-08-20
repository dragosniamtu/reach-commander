using Microsoft.Extensions.Options;
using ReachCommander.Application.Archives;
using ReachCommander.Application.Sources;
using ReachCommander.Domain.Archives;
using ReachCommander.Domain.Sources;
using ReachCommander.Infrastructure.Archives;
using ReachCommander.Infrastructure.Archives.Volumes;
using ReachCommander.Infrastructure.Security;
using ReachCommander.UnitTests.Support;

namespace ReachCommander.UnitTests.Archives;

public sealed class ArchivePartResolverTests : IDisposable
{
    private readonly TemporaryDirectory _temporary = new();
    private readonly string _sourceRoot;

    public ArchivePartResolverTests()
    {
        _sourceRoot = _temporary.CreateDirectory("downloads");
    }

    [Fact]
    public async Task Resolves_modern_rar_parts_in_numeric_order()
    {
        Write("movie.part03.rar", "three");
        Write("movie.part01.rar", "one");
        Write("movie.part02.rar", "two");

        var result = await CreateResolver().ResolveAsync(
            "downloads",
            "/movie.part01.rar",
            CancellationToken.None);

        Assert.Equal(ArchiveFormat.Rar, result.Format);
        Assert.Equal(
            ["/movie.part01.rar", "/movie.part02.rar", "/movie.part03.rar"],
            result.Parts.Select(part => part.LogicalPath));
    }

    [Fact]
    public async Task Resolves_legacy_rar_parts_with_terminal_primary_first()
    {
        Write("movie.rar", "primary");
        Write("movie.r01", "two");
        Write("movie.r00", "one");

        var result = await CreateResolver().ResolveAsync(
            "downloads",
            "/movie.rar",
            CancellationToken.None);

        Assert.Equal(
            ["/movie.rar", "/movie.r00", "/movie.r01"],
            result.Parts.Select(part => part.LogicalPath));
    }

    [Fact]
    public async Task Resolves_numbered_7z_parts_in_numeric_order()
    {
        Write("movie.7z.002", "two");
        Write("movie.7z.001", "one");
        Write("movie.7z.003", "three");

        var result = await CreateResolver().ResolveAsync(
            "downloads",
            "/movie.7z.001",
            CancellationToken.None);

        Assert.Equal(ArchiveFormat.SevenZip, result.Format);
        Assert.Equal(
            ["/movie.7z.001", "/movie.7z.002", "/movie.7z.003"],
            result.Parts.Select(part => part.LogicalPath));
    }

    [Fact]
    public async Task Resolves_numbered_zip_parts_in_numeric_order()
    {
        Write("movie.zip.003", "three");
        Write("movie.zip.001", "one");
        Write("movie.zip.002", "two");

        var result = await CreateResolver().ResolveAsync(
            "downloads",
            "/movie.zip.001",
            CancellationToken.None);

        Assert.Equal(ArchiveFormat.Zip, result.Format);
        Assert.Equal(
            ["/movie.zip.001", "/movie.zip.002", "/movie.zip.003"],
            result.Parts.Select(part => part.LogicalPath));
    }

    [Fact]
    public async Task Resolves_classic_zip_parts_with_terminal_zip_last()
    {
        Write("movie.zip", "last");
        Write("movie.z02", "two");
        Write("movie.z01", "one");

        var result = await CreateResolver().ResolveAsync(
            "downloads",
            "/movie.zip",
            CancellationToken.None);

        Assert.Equal(
            ["/movie.z01", "/movie.z02", "/movie.zip"],
            result.Parts.Select(part => part.LogicalPath));
    }

    [Theory]
    [InlineData("single.zip", ArchiveFormat.Zip)]
    [InlineData("single.rar", ArchiveFormat.Rar)]
    [InlineData("single.7z", ArchiveFormat.SevenZip)]
    public async Task Resolves_standalone_archives_as_one_part(
        string name,
        ArchiveFormat format)
    {
        Write(name, "single");

        var result = await CreateResolver().ResolveAsync(
            "downloads",
            $"/{name}",
            CancellationToken.None);

        Assert.Equal(format, result.Format);
        Assert.Single(result.Parts);
    }

    [Fact]
    public async Task Ignores_unrelated_sibling_files()
    {
        Write("movie.zip", "archive");
        Write("notes.txt", "unrelated");

        var result = await CreateResolver().ResolveAsync(
            "downloads",
            "/movie.zip",
            CancellationToken.None);

        Assert.Single(result.Parts);
    }

    [Theory]
    [InlineData("movie.part02.rar", "/movie.part01.rar")]
    [InlineData("movie.r00", "/movie.rar")]
    [InlineData("movie.7z.002", "/movie.7z.001")]
    [InlineData("movie.zip.002", "/movie.zip.001")]
    [InlineData("movie.z01", "/movie.zip")]
    public async Task Rejects_secondary_input_with_safe_primary_guidance(
        string secondary,
        string expectedPrimary)
    {
        Write(secondary, "secondary");

        var exception = await Assert.ThrowsAsync<ArchiveVolumeSecondaryException>(() =>
            CreateResolver().ResolveAsync(
                "downloads",
                $"/{secondary}",
                CancellationToken.None).AsTask());

        Assert.Equal("archive_volume_secondary", exception.Code);
        Assert.Equal(expectedPrimary, exception.PrimaryLogicalPath);
        Assert.DoesNotContain(_sourceRoot, exception.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rejects_a_missing_middle_part()
    {
        Write("movie.part01.rar", "one");
        Write("movie.part03.rar", "three");

        var exception = await Assert.ThrowsAsync<ArchiveVolumeSetInvalidException>(() =>
            CreateResolver().ResolveAsync(
                "downloads",
                "/movie.part01.rar",
                CancellationToken.None).AsTask());

        Assert.Contains("/movie.part02.rar", exception.ExpectedLogicalNames);
    }

    [Fact]
    public async Task Rejects_duplicate_numeric_indexes()
    {
        Write("movie.part01.rar", "one");
        Write("movie.part001.rar", "duplicate one");

        await Assert.ThrowsAsync<ArchiveVolumeSetInvalidException>(() =>
            CreateResolver().ResolveAsync(
                "downloads",
                "/movie.part01.rar",
                CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Rejects_mixed_volume_schemes_for_one_stem()
    {
        Write("movie.part01.rar", "one");
        Write("movie.part02.rar", "two");
        Write("movie.r00", "mixed");

        await Assert.ThrowsAsync<ArchiveVolumeSetInvalidException>(() =>
            CreateResolver().ResolveAsync(
                "downloads",
                "/movie.part01.rar",
                CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Rejects_a_volume_count_above_the_limit()
    {
        Write("movie.7z.001", "one");
        Write("movie.7z.002", "two");

        var options = new ArchiveOptions { MaxVolumes = 1 };

        await Assert.ThrowsAsync<ArchiveLimitExceededException>(() =>
            CreateResolver(options).ResolveAsync(
                "downloads",
                "/movie.7z.001",
                CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Rejects_compressed_bytes_above_the_limit()
    {
        Write("movie.zip", "12345");
        var options = new ArchiveOptions { MaxTotalCompressedBytes = 4 };

        await Assert.ThrowsAsync<ArchiveLimitExceededException>(() =>
            CreateResolver(options).ResolveAsync(
                "downloads",
                "/movie.zip",
                CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Rejects_a_symbolic_link_part()
    {
        var target = Write("real.zip", "archive");
        var link = Path.Combine(_sourceRoot, "linked.zip");
        if (!TryCreateFileLink(link, target))
        {
            return;
        }

        await Assert.ThrowsAsync<ArchiveVolumeSetInvalidException>(() =>
            CreateResolver().ResolveAsync(
                "downloads",
                "/linked.zip",
                CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Fingerprint_changes_when_part_metadata_changes()
    {
        var path = Write("movie.7z", "one");
        var resolver = CreateResolver();
        var before = await resolver.ResolveAsync(
            "downloads",
            "/movie.7z",
            CancellationToken.None);

        await File.AppendAllTextAsync(path, "-changed");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        var after = await resolver.ResolveAsync(
            "downloads",
            "/movie.7z",
            CancellationToken.None);

        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
    }

    public void Dispose() => _temporary.Dispose();

    private string Write(string name, string content) =>
        _temporary.Write($"downloads/{name}", content);

    private ArchivePartResolver CreateResolver(ArchiveOptions? options = null)
    {
        var source = new SourceDefinition(
            "downloads",
            "Downloads",
            _sourceRoot,
            IsReadOnly: false,
            DefaultLeft: true,
            DefaultRight: false);
        var pathSecurity = new PathSecurityService(new FakeSourceCatalog(source));
        return new ArchivePartResolver(pathSecurity, Options.Create(options ?? new ArchiveOptions()));
    }

    private static bool TryCreateFileLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return false;
        }
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
            CancellationToken cancellationToken) => ValueTask.FromResult(source);
    }
}

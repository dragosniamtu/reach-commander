using Microsoft.Extensions.Options;
using ReachCommander.Application.Archives;
using ReachCommander.Domain.Archives;
using ReachCommander.Infrastructure.Archives;
using ReachCommander.Infrastructure.Archives.Catalog;

namespace ReachCommander.UnitTests.Archives;

public sealed class ArchiveCatalogBuilderTests
{
    [Fact]
    public void Synthesizes_missing_directories_and_lists_only_immediate_children()
    {
        var catalog = CreateBuilder().Build(
            ArchiveFormat.Zip,
            [Entry(7, "a/b/file.txt", size: 1_024, compressedSize: 512)]);

        var root = Assert.Single(catalog.ListChildren("/"));
        Assert.Equal("/a", root.Path);
        Assert.Equal(ArchiveEntryType.Directory, root.Type);

        var a = Assert.Single(catalog.ListChildren("/a"));
        Assert.Equal("/a/b", a.Path);
        Assert.Equal(1, a.DescendantFileCount);
        Assert.Equal(1_024, a.DescendantSize);

        var b = Assert.Single(catalog.ListChildren("/a/b"));
        Assert.Equal("/a/b/file.txt", b.Path);
        Assert.Equal(7, b.WorkerEntryIndex);
        Assert.Equal("txt", b.Extension);
        Assert.Equal("Archive · RO", b.Attributes);
    }

    [Fact]
    public void Merges_an_explicit_directory_with_its_synthesized_copy()
    {
        var catalog = CreateBuilder().Build(
            ArchiveFormat.Zip,
            [
                Entry(1, "a/b/file.txt", size: 1, compressedSize: 1),
                Entry(2, "a/", isDirectory: true),
            ]);

        Assert.Single(catalog.Nodes.Values, node => node.Path == "/a");
        Assert.Equal(2, catalog.DirectoryCount);
    }

    [Theory]
    [MemberData(nameof(CollidingEntries))]
    public void Rejects_duplicate_case_normalization_and_ancestor_collisions(
        object value)
    {
        var entries = Assert.IsType<UntrustedArchiveEntry[]>(value);

        Assert.Throws<ArchiveEntryUnsafeException>(() =>
            CreateBuilder().Build(ArchiveFormat.Zip, entries));
    }

    public static TheoryData<object> CollidingEntries => new()
    {
        new[] { Entry(1, "a.txt"), Entry(2, "a.txt") },
        new[] { Entry(1, "A.txt"), Entry(2, "a.txt") },
        new[] { Entry(1, "Caf\u00e9.txt"), Entry(2, "Cafe\u0301.txt") },
        new[] { Entry(1, "a", isDirectory: false), Entry(2, "a/b.txt") },
        new[] { Entry(1, "a", isDirectory: false), Entry(2, "a", isDirectory: true) },
    };

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Rejects_link_and_special_entries(bool isLink, bool isSpecial)
    {
        var entry = Entry(1, "unsafe", isLink: isLink, isSpecial: isSpecial);

        Assert.Throws<ArchiveEntryUnsafeException>(() =>
            CreateBuilder().Build(ArchiveFormat.Rar, [entry]));
    }

    [Fact]
    public void Rejects_encrypted_entries_before_exposure()
    {
        var entry = Entry(1, "secret.txt") with { IsEncrypted = true };

        Assert.Throws<ArchiveEncryptedException>(() =>
            CreateBuilder().Build(ArchiveFormat.SevenZip, [entry]));
    }

    [Fact]
    public void Rejects_entry_count_above_the_limit_including_synthesized_directories()
    {
        var builder = CreateBuilder(new ArchiveOptions { MaxEntries = 2, MaxCachedEntries = 2 });

        Assert.Throws<ArchiveLimitExceededException>(() => builder.Build(
            ArchiveFormat.Zip,
            [Entry(1, "a/b/file.txt")]));
    }

    [Fact]
    public void Rejects_single_and_total_extracted_size_limits()
    {
        var single = CreateBuilder(new ArchiveOptions
        {
            MaxSingleExtractedFileBytes = 5,
            MaxTotalExtractedBytes = 10,
        });
        var total = CreateBuilder(new ArchiveOptions
        {
            MaxSingleExtractedFileBytes = 10,
            MaxTotalExtractedBytes = 10,
        });

        Assert.Throws<ArchiveLimitExceededException>(() => single.Build(
            ArchiveFormat.Zip,
            [Entry(1, "large.bin", size: 6, compressedSize: 1)]));
        Assert.Throws<ArchiveLimitExceededException>(() => total.Build(
            ArchiveFormat.Zip,
            [
                Entry(1, "one.bin", size: 6, compressedSize: 2),
                Entry(2, "two.bin", size: 6, compressedSize: 2),
            ]));
    }

    [Fact]
    public void Rejects_checked_total_size_overflow_as_a_limit_breach()
    {
        var builder = CreateBuilder(new ArchiveOptions
        {
            MaxSingleExtractedFileBytes = long.MaxValue,
            MaxTotalExtractedBytes = long.MaxValue,
            MaxExpansionRatio = int.MaxValue,
        });

        Assert.Throws<ArchiveLimitExceededException>(() => builder.Build(
            ArchiveFormat.Zip,
            [
                Entry(1, "one.bin", size: long.MaxValue, compressedSize: long.MaxValue),
                Entry(2, "two.bin", size: 1, compressedSize: 1),
            ]));
    }

    [Fact]
    public void Rejects_known_expansion_ratio_above_the_limit()
    {
        var builder = CreateBuilder(new ArchiveOptions { MaxExpansionRatio = 2 });

        Assert.Throws<ArchiveLimitExceededException>(() => builder.Build(
            ArchiveFormat.Zip,
            [Entry(1, "bomb.bin", size: 5, compressedSize: 2)]));
        Assert.Throws<ArchiveLimitExceededException>(() => builder.Build(
            ArchiveFormat.Zip,
            [Entry(1, "zero.bin", size: 1, compressedSize: 0)]));
    }

    [Fact]
    public void Preserves_unknown_declared_size_and_marks_aggregate_unknown()
    {
        var catalog = CreateBuilder().Build(
            ArchiveFormat.Zip,
            [Entry(1, "unknown.bin", size: null, compressedSize: null)]);

        var file = Assert.Single(catalog.ListChildren("/"));
        Assert.Null(file.Size);
        Assert.Null(catalog.TotalDeclaredSize);
    }

    [Fact]
    public void Expands_selection_once_and_collapses_descendant_roots()
    {
        var catalog = CreateBuilder().Build(
            ArchiveFormat.Zip,
            [
                Entry(1, "Family/one.txt", size: 1, compressedSize: 1),
                Entry(2, "Family/Child/two.txt", size: 1, compressedSize: 1),
                Entry(3, "other.txt", size: 1, compressedSize: 1),
            ]);

        var expanded = catalog.ExpandSelection(
            "/",
            ["/Family", "/Family/Child/two.txt"],
            extractAll: false);

        Assert.Equal(
            ["/Family", "/Family/Child", "/Family/Child/two.txt", "/Family/one.txt"],
            expanded.Select(node => node.Path));
    }

    [Fact]
    public void Extract_all_returns_every_root_descendant_once()
    {
        var catalog = CreateBuilder().Build(
            ArchiveFormat.Zip,
            [
                Entry(1, "folder/one.txt", size: 1, compressedSize: 1),
                Entry(2, "root.txt", size: 1, compressedSize: 1),
            ]);

        var expanded = catalog.ExpandSelection("/", [], extractAll: true);

        Assert.Equal(
            ["/folder", "/folder/one.txt", "/root.txt"],
            expanded.Select(node => node.Path));
    }

    private static ArchiveCatalogBuilder CreateBuilder(ArchiveOptions? options = null) =>
        new(Options.Create(options ?? new ArchiveOptions()));

    private static UntrustedArchiveEntry Entry(
        int index,
        string key,
        bool isDirectory = false,
        bool isLink = false,
        bool isSpecial = false,
        long? size = 1,
        long? compressedSize = 1) =>
        new(
            Index: index,
            Key: key,
            IsDirectory: isDirectory,
            IsEncrypted: false,
            IsLink: isLink,
            IsSpecial: isSpecial,
            Size: isDirectory ? null : size,
            CompressedSize: isDirectory ? null : compressedSize,
            ModifiedAt: DateTimeOffset.Parse("2026-08-20T10:00:00Z"));
}

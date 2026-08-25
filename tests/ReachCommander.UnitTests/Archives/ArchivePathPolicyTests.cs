using Microsoft.Extensions.Options;
using System.Text;
using ReachCommander.Application.Archives;
using ReachCommander.Infrastructure.Archives;
using ReachCommander.Infrastructure.Archives.Catalog;

namespace ReachCommander.UnitTests.Archives;

public sealed class ArchivePathPolicyTests
{
    [Theory]
    [InlineData("")]
    [InlineData("../escape")]
    [InlineData("/rooted")]
    [InlineData("C:/drive")]
    [InlineData("C:\\drive")]
    [InlineData("\\\\server\\share")]
    [InlineData("a/./b")]
    [InlineData("a/../b")]
    [InlineData("a//b")]
    [InlineData("a:b")]
    [InlineData("name.")]
    [InlineData("name ")]
    [InlineData("CON")]
    [InlineData("aux.txt")]
    [InlineData(".reachcommander-extract-forged.partial/file.txt")]
    [InlineData(".reachcommander-trash/items/file.txt")]
    [InlineData("folder/.reachcommander-operation-123-stage/file.txt")]
    public void Rejects_unsafe_entry_paths(string value)
    {
        var policy = CreatePolicy();

        Assert.Throws<ArchiveEntryUnsafeException>(() => policy.NormalizeEntryPath(value));
    }

    [Fact]
    public void Rejects_null_and_control_characters()
    {
        var policy = CreatePolicy();

        Assert.Throws<ArchiveEntryUnsafeException>(() => policy.NormalizeEntryPath("a\0b"));
        Assert.Throws<ArchiveEntryUnsafeException>(() => policy.NormalizeEntryPath("a\u001fb"));
    }

    [Fact]
    public void Rejects_depth_above_the_limit()
    {
        var policy = CreatePolicy(new ArchiveOptions { MaxPathDepth = 2 });

        Assert.Throws<ArchiveLimitExceededException>(() =>
            policy.NormalizeEntryPath("one/two/three"));
    }

    [Fact]
    public void Rejects_component_above_the_limit()
    {
        var policy = CreatePolicy(new ArchiveOptions { MaxComponentCharacters = 3 });

        Assert.Throws<ArchiveLimitExceededException>(() =>
            policy.NormalizeEntryPath("four"));
    }

    [Fact]
    public void Rejects_full_path_above_the_limit()
    {
        var policy = CreatePolicy(new ArchiveOptions
        {
            MaxPathCharacters = 5,
            MaxComponentCharacters = 5,
        });

        Assert.Throws<ArchiveLimitExceededException>(() =>
            policy.NormalizeEntryPath("abcde"));
    }

    [Fact]
    public void Normalizes_separators_and_unicode_to_NFC()
    {
        var policy = CreatePolicy();
        const string decomposed = "Cafe\u0301\\photo.txt";

        var result = policy.NormalizeEntryPath(decomposed);

        Assert.Equal("/Caf\u00e9/photo.txt", result);
        Assert.True(result.IsNormalized(NormalizationForm.FormC));
    }

    private static ArchivePathPolicy CreatePolicy(ArchiveOptions? options = null) =>
        new(Options.Create(options ?? new ArchiveOptions()));
}

using ReachCommander.Domain.Archives;
using ReachCommander.Infrastructure.Archives.Classification;

namespace ReachCommander.UnitTests.Archives;

public sealed class ArchiveFilenameClassifierTests
{
    [Theory]
    [InlineData("photos.zip", ArchiveFormat.Zip, ArchiveRole.Single)]
    [InlineData("photos.7z", ArchiveFormat.SevenZip, ArchiveRole.Single)]
    [InlineData("photos.rar", ArchiveFormat.Rar, ArchiveRole.Single)]
    [InlineData("photos.part01.rar", ArchiveFormat.Rar, ArchiveRole.Primary)]
    [InlineData("photos.part02.rar", ArchiveFormat.Rar, ArchiveRole.Secondary)]
    [InlineData("photos.r00", ArchiveFormat.Rar, ArchiveRole.Secondary)]
    [InlineData("photos.7z.001", ArchiveFormat.SevenZip, ArchiveRole.Primary)]
    [InlineData("photos.7z.002", ArchiveFormat.SevenZip, ArchiveRole.Secondary)]
    [InlineData("photos.zip.001", ArchiveFormat.Zip, ArchiveRole.Primary)]
    [InlineData("photos.z01", ArchiveFormat.Zip, ArchiveRole.Secondary)]
    public void Classifies_supported_single_and_volume_names(
        string name,
        ArchiveFormat format,
        ArchiveRole role)
    {
        var result = ArchiveFilenameClassifier.Classify(name, isLink: false);

        Assert.NotNull(result);
        Assert.Equal(format, result.Format);
        Assert.Equal(role, result.Role);
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("photos.part00.rar")]
    [InlineData("photos.7z.000")]
    [InlineData("photos.zip.000")]
    [InlineData("photos.z00")]
    public void Rejects_unsupported_names(string name)
        => Assert.Null(ArchiveFilenameClassifier.Classify(name, isLink: false));

    [Fact]
    public void Never_marks_a_link_as_openable()
        => Assert.Null(ArchiveFilenameClassifier.Classify("photos.zip", isLink: true));

    [Fact]
    public void Directory_context_upgrades_legacy_terminal_parts_to_primary()
    {
        var siblings = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "legacy.rar",
            "legacy.r00",
            "classic.z01",
            "classic.zip",
        };

        var rar = ArchiveFilenameClassifier.Classify("legacy.rar", isLink: false, siblings);
        var zip = ArchiveFilenameClassifier.Classify("classic.zip", isLink: false, siblings);

        Assert.Equal(ArchiveRole.Primary, rar!.Role);
        Assert.Equal(ArchiveRole.Primary, zip!.Role);
    }
}

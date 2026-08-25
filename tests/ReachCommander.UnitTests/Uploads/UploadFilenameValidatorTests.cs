using ReachCommander.Application.Uploads;
using ReachCommander.Infrastructure.Uploads;

namespace ReachCommander.UnitTests.Uploads;

public sealed class UploadFilenameValidatorTests
{
    private readonly UploadFilenameValidator _validator = new();

    [Theory]
    [InlineData("movie.mkv")]
    [InlineData(".env")]
    [InlineData("Résumé 2026.pdf")]
    [InlineData("zero-byte")]
    [InlineData("COM0.txt")]
    public void Validate_preserves_safe_names(string name) =>
        Assert.Equal(name, _validator.Validate(name));

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../escape.txt")]
    [InlineData("folder/file.txt")]
    [InlineData("folder\\file.txt")]
    [InlineData("C:\\file.txt")]
    [InlineData("\\\\server\\share.txt")]
    [InlineData("CON.txt")]
    [InlineData("com9.log")]
    [InlineData("LPT1")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("nul\0name")]
    [InlineData("bad<name")]
    [InlineData("bad>name")]
    [InlineData("bad:name")]
    [InlineData("bad\"name")]
    [InlineData("bad|name")]
    [InlineData("bad?name")]
    [InlineData("bad*name")]
    [InlineData("control\u001fname")]
    [InlineData(".reachcommander-trash")]
    [InlineData(".reachcommander-operation-123-stage")]
    public void Validate_rejects_nonportable_or_path_bearing_names(string name) =>
        Assert.Throws<UploadNameInvalidException>(() => _validator.Validate(name));

    [Fact]
    public void Validate_rejects_names_over_255_utf8_bytes()
    {
        var name = $"{new string('é', 126)}.txt";

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(name) > 255);
        Assert.Throws<UploadNameInvalidException>(() => _validator.Validate(name));
    }
}

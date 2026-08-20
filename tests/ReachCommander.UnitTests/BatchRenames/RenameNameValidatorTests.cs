using ReachCommander.Infrastructure.BatchRenames;

namespace ReachCommander.UnitTests.BatchRenames;

public sealed class RenameNameValidatorTests
{
    private readonly RenameNameValidator _validator = new();

    [Theory]
    [InlineData("movie.mkv")]
    [InlineData("Résumé 2026.txt")]
    [InlineData(".env")]
    [InlineData("zero-byte")]
    public void Validate_accepts_portable_names(string name) =>
        Assert.True(_validator.Validate(name).IsValid);

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("CON")]
    [InlineData("lpt1.txt")]
    [InlineData("bad/name")]
    [InlineData("bad\\name")]
    [InlineData("bad:name")]
    [InlineData("bad<name")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("control\u001fname")]
    public void Validate_rejects_non_portable_or_reserved_names(string name)
    {
        var validation = _validator.Validate(name);

        Assert.False(validation.IsValid);
        Assert.False(string.IsNullOrWhiteSpace(validation.Message));
    }

    [Fact]
    public void Validate_enforces_the_utf8_component_limit()
    {
        Assert.True(_validator.Validate(new string('a', 255)).IsValid);
        Assert.False(_validator.Validate(new string('é', 128)).IsValid);
    }
}

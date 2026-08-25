using ReachCommander.Infrastructure.FileOperations.Planning;

namespace ReachCommander.UnitTests.FileOperations;

public sealed class UniqueNamePolicyTests
{
    [Fact]
    public void Find_inserts_suffix_before_final_extension()
    {
        var result = UniqueNamePolicy.Find(
            "/target/file.txt",
            path => path is "/target/file.txt" or "/target/file (2).txt");

        Assert.Equal("/target/file (3).txt", result);
    }

    [Theory]
    [InlineData("/target/Folder", "/target/Folder (2)")]
    [InlineData("/target/archive.tar.gz", "/target/archive.tar (2).gz")]
    [InlineData("/target/.env", "/target/.env (2)")]
    public void Find_handles_directories_and_final_extensions(string requested, string expected)
    {
        var result = UniqueNamePolicy.Find(requested, path => path == requested);

        Assert.Equal(expected, result);
    }
}

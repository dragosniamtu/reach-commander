using ReachCommander.Infrastructure.FileOperations;

namespace ReachCommander.UnitTests.Files;

public sealed class ReservedFileOperationPathPolicyTests
{
    [Theory]
    [InlineData(".reachcommander-trash")]
    [InlineData(".REACHCOMMANDER-TRASH")]
    [InlineData(".reachcommander-operation-7b97-stage")]
    [InlineData(".ReachCommander-Operation-7B97-Quarantine")]
    public void IsReservedName_rejects_operation_owned_names(string name) =>
        Assert.True(ReservedFileOperationPathPolicy.IsReservedName(name));

    [Theory]
    [InlineData("movie.mkv")]
    [InlineData(".hidden")]
    [InlineData("reachcommander-operation-not-hidden")]
    public void IsReservedName_preserves_normal_names(string name) =>
        Assert.False(ReservedFileOperationPathPolicy.IsReservedName(name));

    [Theory]
    [InlineData("/.reachcommander-trash/items")]
    [InlineData("/movies/.reachcommander-operation-7b97-stage/file.bin")]
    public void ContainsReservedSegment_detects_nested_internal_paths(string path) =>
        Assert.True(ReservedFileOperationPathPolicy.ContainsReservedSegment(path));
}

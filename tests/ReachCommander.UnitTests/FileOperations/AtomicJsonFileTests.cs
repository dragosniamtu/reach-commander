using ReachCommander.Infrastructure.FileOperations.Persistence;
using ReachCommander.UnitTests.Support;

namespace ReachCommander.UnitTests.FileOperations;

public sealed class AtomicJsonFileTests : IDisposable
{
    private readonly TemporaryDirectory _temporary = new();

    [Fact]
    public async Task WriteAsync_replaces_document_and_leaves_no_temporary_file()
    {
        var path = Path.Combine(_temporary.Path, "state", "document.json");

        await AtomicJsonFile.WriteAsync(path, new SampleDocument(1, "first"), default);
        await AtomicJsonFile.WriteAsync(path, new SampleDocument(1, "second"), default);
        var loaded = await AtomicJsonFile.ReadAsync<SampleDocument>(path, default);

        Assert.Equal(new SampleDocument(1, "second"), loaded);
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp-*"));
    }

    [Fact]
    public async Task ReadAsync_rejects_unmapped_json_members()
    {
        var path = _temporary.Write(
            "document.json",
            "{\"schemaVersion\":1,\"value\":\"ok\",\"unexpected\":true}");

        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() =>
            AtomicJsonFile.ReadAsync<SampleDocument>(path, default));
    }

    public void Dispose() => _temporary.Dispose();

    private sealed record SampleDocument(int SchemaVersion, string Value);
}

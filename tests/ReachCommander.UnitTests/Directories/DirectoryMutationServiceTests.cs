using ReachCommander.Application.Directories;
using ReachCommander.Application.FileOperations;
using ReachCommander.Application.Sources;
using ReachCommander.Domain.Sources;
using ReachCommander.Infrastructure.BatchRenames;
using ReachCommander.Infrastructure.Directories;
using ReachCommander.Infrastructure.FileSystem;
using ReachCommander.Infrastructure.FileOperations.Planning;
using ReachCommander.Infrastructure.Mutations;
using ReachCommander.Infrastructure.Security;
using ReachCommander.UnitTests.Support;

namespace ReachCommander.UnitTests.Directories;

public sealed class DirectoryMutationServiceTests : IDisposable
{
    private readonly TemporaryDirectory _temporary = new();
    private readonly string _writableRoot;
    private readonly DirectoryMutationService _service;

    public DirectoryMutationServiceTests()
    {
        _writableRoot = _temporary.CreateDirectory("writable");
        var readOnlyRoot = _temporary.CreateDirectory("readonly");
        var sources = new FakeSourceCatalog(
            new("writable", "Writable", _writableRoot, false, true, false),
            new("readonly", "Read only", readOnlyRoot, true, false, true));
        var security = new PathSecurityService(sources);
        _service = new DirectoryMutationService(
            sources,
            security,
            new LocalFileBrowser(security),
            new LocalFileOperationInspector(security),
            new DirectoryMutationLock(),
            new RenameNameValidator());
    }

    [Fact]
    public async Task Creates_one_exact_child_and_returns_logical_entry()
    {
        var result = await _service.CreateAsync(
            new("writable", "/", "Family"),
            default);

        Assert.Equal("/Family", result.RelativePath);
        Assert.Equal("Family", result.Name);
        Assert.True(Directory.Exists(Path.Combine(_writableRoot, "Family")));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("A/B")]
    [InlineData(".reachcommander-trash")]
    [InlineData("CON")]
    public async Task Rejects_invalid_or_reserved_names(string name)
    {
        await Assert.ThrowsAsync<InvalidDirectoryNameException>(() =>
            _service.CreateAsync(new("writable", "/", name), default));
    }

    [Fact]
    public async Task Rejects_read_only_source()
    {
        await Assert.ThrowsAsync<OperationSourceReadOnlyException>(() =>
            _service.CreateAsync(new("readonly", "/", "Family"), default));
    }

    [Fact]
    public async Task Rejects_existing_destination_without_changing_it()
    {
        Directory.CreateDirectory(Path.Combine(_writableRoot, "Family"));

        await Assert.ThrowsAsync<DestinationConflictException>(() =>
            _service.CreateAsync(new("writable", "/", "Family"), default));

        Assert.True(Directory.Exists(Path.Combine(_writableRoot, "Family")));
    }

    public void Dispose() => _temporary.Dispose();

    private sealed class FakeSourceCatalog(params SourceDefinition[] sources) : ISourceCatalog
    {
        public ValueTask<IReadOnlyList<SourceDefinition>> GetDefinitionsAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<SourceDefinition>>(sources);

        public ValueTask<IReadOnlyList<SourceSnapshot>> GetSnapshotsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<SourceDefinition> GetRequiredAsync(string sourceId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(sources.Single(source => source.Id == sourceId));
    }
}

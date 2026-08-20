using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReachCommander.Application.Files;
using ReachCommander.Application.Sources;
using ReachCommander.Application.Uploads;
using ReachCommander.Domain.Sources;
using ReachCommander.Infrastructure.Mutations;
using ReachCommander.Infrastructure.Security;
using ReachCommander.Infrastructure.Uploads;

namespace ReachCommander.UnitTests.Support;

internal sealed class UploadTestFixture : IAsyncDisposable
{
    private readonly TemporaryDirectory _temporary = new();
    private readonly SourceDefinition _source;
    private readonly IPathSecurityService _pathSecurity;
    private readonly List<UploadService> _services = [];

    public UploadTestFixture(
        bool readOnly = false,
        UploadOptions? options = null,
        IUploadFileSystem? fileSystem = null,
        DirectoryMutationLock? mutationLock = null,
        Func<string, SourceDefinition, IPathSecurityService>? pathSecurityFactory = null)
    {
        SourceRoot = _temporary.CreateDirectory("media");
        Directory.CreateDirectory(System.IO.Path.Combine(SourceRoot, "Movies"));
        _source = new SourceDefinition(
            "media",
            "Media",
            SourceRoot,
            readOnly,
            DefaultLeft: true,
            DefaultRight: false);
        Options = options ?? new UploadOptions();
        _pathSecurity = pathSecurityFactory?.Invoke(SourceRoot, _source) ??
            new PathSecurityService(new FakeSourceCatalog(_source));
        Service = CreateService(fileSystem, mutationLock);
    }

    public string SourceRoot { get; }

    public UploadOptions Options { get; }

    public UploadService Service { get; }

    public UploadService CreateService(
        IUploadFileSystem? fileSystem = null,
        DirectoryMutationLock? mutationLock = null)
    {
        var service = new UploadService(
            _pathSecurity,
            new UploadFilenameValidator(),
            mutationLock ?? new DirectoryMutationLock(),
            fileSystem ?? new LocalUploadFileSystem(),
            Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<UploadService>.Instance);
        _services.Add(service);
        return service;
    }

    public string Read(string relativePath) =>
        File.ReadAllText(System.IO.Path.Combine(SourceRoot, relativePath));

    public void Write(string relativePath, string content)
    {
        var path = System.IO.Path.Combine(SourceRoot, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public bool Exists(string relativePath) =>
        File.Exists(System.IO.Path.Combine(SourceRoot, relativePath));

    public string CreateDirectory(string relativePath) => _temporary.CreateDirectory(relativePath);

    public string[] StagingEntries(string relativeDirectory) =>
        Directory.GetFileSystemEntries(System.IO.Path.Combine(SourceRoot, relativeDirectory), ".reachcommander-upload-*.partial");

    public ValueTask DisposeAsync()
    {
        foreach (var service in _services)
        {
            service.Dispose();
        }

        _temporary.Dispose();
        return ValueTask.CompletedTask;
    }

    public static async IAsyncEnumerable<UploadFilePart> Parts(
        params (string Name, string Content)[] files)
    {
        foreach (var (name, content) in files)
        {
            await Task.Yield();
            var bytes = System.Text.Encoding.UTF8.GetBytes(content);
            yield return new UploadFilePart(name, new MemoryStream(bytes), bytes.LongLength);
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
            CancellationToken cancellationToken) =>
            sourceId.Equals(source.Id, StringComparison.OrdinalIgnoreCase)
                ? ValueTask.FromResult(source)
                : ValueTask.FromException<SourceDefinition>(new SourceNotFoundException(sourceId));
    }
}

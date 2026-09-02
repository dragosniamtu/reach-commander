using System.Text;
using ReachCommander.Application.Sources;
using ReachCommander.Application.TextEncodings;
using ReachCommander.Domain.Sources;
using ReachCommander.Infrastructure.Security;
using ReachCommander.Infrastructure.TextEncodings;

namespace ReachCommander.UnitTests.Support;

internal sealed class TextEncodingTestFixture : IDisposable
{
    private readonly TemporaryDirectory _temporary = new();
    private readonly SourceDefinition _source;
    private readonly TestTextEncodingFileSystem _fileSystem = new();

    public TextEncodingTestFixture()
    {
        SourceRoot = _temporary.CreateDirectory("media");
        Directory.CreateDirectory(Path.Combine(SourceRoot, "TV"));
        _source = new SourceDefinition(
            "media",
            "Media",
            SourceRoot,
            IsReadOnly: false,
            DefaultLeft: true,
            DefaultRight: false);
        Clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 2, 8, 0, 0, TimeSpan.Zero));
        PlanStore = new TextEncodingPlanStore(Clock);
        Planner = CreatePlanner();
    }

    public string SourceRoot { get; }

    public ManualTimeProvider Clock { get; }

    public TextEncodingPlanStore PlanStore { get; }

    public TextEncodingPlanner Planner { get; }

    public TextEncodingPlanner CreatePlanner(bool sourceReadOnly = false)
    {
        var source = _source with { IsReadOnly = sourceReadOnly };
        var paths = new PathSecurityService(new FakeSourceCatalog(source));
        return new TextEncodingPlanner(paths, _fileSystem, PlanStore, Clock);
    }

    public void WriteUtf8(string relativePath, string contents) =>
        WriteBytes(relativePath, new UTF8Encoding(false, true).GetBytes(contents));

    public void WriteWindows1250(string relativePath, string contents)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        WriteBytes(relativePath, Encoding.GetEncoding(1250).GetBytes(contents));
    }

    public void WriteBytes(string relativePath, byte[] contents)
    {
        var path = PhysicalPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, contents);
    }

    public void CreateSizedFile(string relativePath, long length)
    {
        var path = PhysicalPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.SetLength(length);
    }

    public void CreateDirectory(string relativePath) => Directory.CreateDirectory(PhysicalPath(relativePath));

    public void MarkAsSymbolicLink(string relativePath) =>
        _fileSystem.MarkAsSymbolicLink(LogicalPath(relativePath));

    public string PhysicalPath(string relativePath) => Path.Combine(
        SourceRoot,
        relativePath.Replace('/', Path.DirectorySeparatorChar));

    public static string LogicalPath(string relativePath) => $"/{relativePath.Replace('\\', '/')}";

    public void Dispose() => _temporary.Dispose();

    internal sealed class ManualTimeProvider(DateTimeOffset current) : TimeProvider
    {
        private DateTimeOffset _current = current;

        public override DateTimeOffset GetUtcNow() => _current;

        public void Advance(TimeSpan amount) => _current += amount;
    }

    private sealed class TestTextEncodingFileSystem : ITextEncodingFileSystem
    {
        private readonly LocalTextEncodingFileSystem _inner = new();
        private readonly HashSet<string> _symbolicLinks = new(StringComparer.Ordinal);

        public void MarkAsSymbolicLink(string logicalPath) => _symbolicLinks.Add(logicalPath);

        public async ValueTask<TextFileSnapshot> ReadSnapshotAsync(
            string logicalPath,
            string physicalPath,
            bool pathTraversedSymbolicLink,
            CancellationToken cancellationToken)
        {
            var snapshot = await _inner.ReadSnapshotAsync(
                logicalPath,
                physicalPath,
                pathTraversedSymbolicLink,
                cancellationToken);
            return _symbolicLinks.Contains(logicalPath)
                ? snapshot with { IsSymbolicLink = true }
                : snapshot;
        }

        public Task WriteNewAsync(
            string physicalPath,
            ReadOnlyMemory<byte> contents,
            CancellationToken cancellationToken) =>
            _inner.WriteNewAsync(physicalPath, contents, cancellationToken);

        public void MoveFile(string sourcePhysicalPath, string destinationPhysicalPath) =>
            _inner.MoveFile(sourcePhysicalPath, destinationPhysicalPath);

        public void DeleteFile(string physicalPath) => _inner.DeleteFile(physicalPath);

        public bool FileExists(string physicalPath) => _inner.FileExists(physicalPath);

        public IReadOnlyList<string> ListNames(string directoryPhysicalPath) =>
            _inner.ListNames(directoryPhysicalPath);

        public void FlushDirectory(string directoryPhysicalPath) =>
            _inner.FlushDirectory(directoryPhysicalPath);
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

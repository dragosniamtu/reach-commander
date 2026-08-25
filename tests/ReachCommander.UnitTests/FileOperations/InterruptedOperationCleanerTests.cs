using ReachCommander.Application.FileOperations;
using ReachCommander.Application.Files;
using ReachCommander.Application.Sources;
using ReachCommander.Domain.Sources;
using ReachCommander.Infrastructure.FileOperations;
using ReachCommander.Infrastructure.FileOperations.Execution;
using ReachCommander.Infrastructure.FileOperations.Persistence;
using ReachCommander.Infrastructure.FileOperations.Planning;
using ReachCommander.Infrastructure.Security;
using ReachCommander.UnitTests.Support;

namespace ReachCommander.UnitTests.FileOperations;

public sealed class InterruptedOperationCleanerTests : IDisposable
{
    private readonly TemporaryDirectory _temporary = new();
    private readonly string _root;
    private readonly RecoveryFileSystem _fileSystem;
    private readonly InterruptedOperationCleaner _cleaner;

    public InterruptedOperationCleanerTests()
    {
        _root = _temporary.CreateDirectory("source");
        var sources = new FakeSourceCatalog(
            new SourceDefinition("media", "Media", _root, true, true, false));
        var pathSecurity = new PathSecurityService(sources);
        _fileSystem = new RecoveryFileSystem(new LocalFileOperationFileSystem());
        _cleaner = new InterruptedOperationCleaner(pathSecurity, _fileSystem);
    }

    [Fact]
    public async Task Deletes_exact_allowlisted_staging_entry()
    {
        var operationId = Guid.NewGuid();
        var ownedName = OwnedName(operationId, "stage");
        await File.WriteAllTextAsync(Path.Combine(_root, ownedName), "partial");

        var warnings = await _cleaner.CleanupAsync(
            Document(
                operationId,
                new FileOperationJournalEntry("media", "/", ownedName, "/movie.mkv", false)),
            default);

        Assert.Empty(warnings);
        Assert.False(File.Exists(Path.Combine(_root, ownedName)));
    }

    [Fact]
    public async Task Restores_quarantine_only_when_public_destination_is_absent()
    {
        var operationId = Guid.NewGuid();
        var ownedName = OwnedName(operationId, "quarantine");
        await File.WriteAllTextAsync(Path.Combine(_root, ownedName), "old");

        var warnings = await _cleaner.CleanupAsync(
            Document(
                operationId,
                new FileOperationJournalEntry("media", "/", ownedName, "/movie.mkv", true)),
            default);

        Assert.Empty(warnings);
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(_root, "movie.mkv")));
        Assert.False(File.Exists(Path.Combine(_root, ownedName)));
    }

    [Fact]
    public async Task Refuses_malformed_out_of_scope_and_link_entries()
    {
        var operationId = Guid.NewGuid();
        var outside = Path.Combine(_temporary.Path, "outside.txt");
        await File.WriteAllTextAsync(outside, "keep");
        var linkedName = OwnedName(operationId, "stage");
        var linkedPath = Path.Combine(_root, linkedName);
        await File.WriteAllTextAsync(linkedPath, "keep-link");
        _fileSystem.AttributeOverrides[Path.GetFullPath(linkedPath)] = FileAttributes.ReparsePoint;

        var warnings = await _cleaner.CleanupAsync(
            Document(
                operationId,
                new FileOperationJournalEntry("media", "/", "../outside.txt", null, false),
                new FileOperationJournalEntry("media", "/", linkedName, null, false)),
            default);

        Assert.Equal(2, warnings.Count);
        Assert.Equal("keep", await File.ReadAllTextAsync(outside));
        Assert.Equal("keep-link", await File.ReadAllTextAsync(linkedPath));
    }

    public void Dispose() => _temporary.Dispose();

    private static PersistedFileOperationDocument Document(
        Guid operationId,
        params FileOperationJournalEntry[] entries)
    {
        var now = DateTimeOffset.UtcNow;
        var plan = new FileOperationPlan(
            Guid.NewGuid(),
            now,
            now.AddMinutes(1),
            FileOperationKind.Move,
            "media",
            ["/movie.mkv"],
            "media",
            "/",
            [],
            [],
            null,
            [],
            [],
            0);
        var status = new FileOperationStatus(
            operationId,
            FileOperationKind.Move,
            FileOperationPhase.Interrupted,
            0,
            now,
            now,
            new(null, 0, 0, 0, 0, 0, null, TimeSpan.Zero, null),
            [],
            [],
            false);
        return new(
            FileOperationSchema.CurrentVersion,
            1,
            plan,
            new([], false),
            status,
            false,
            new(operationId, entries));
    }

    private static string OwnedName(Guid operationId, string purpose) =>
        $"{ReservedFileOperationPathPolicy.OperationPrefix}{operationId:N}-{purpose}-{Guid.NewGuid():N}";

    private sealed class FakeSourceCatalog(params SourceDefinition[] sources) : ISourceCatalog
    {
        public ValueTask<IReadOnlyList<SourceDefinition>> GetDefinitionsAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<SourceDefinition>>(sources);

        public ValueTask<IReadOnlyList<SourceSnapshot>> GetSnapshotsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<SourceDefinition> GetRequiredAsync(string sourceId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(sources.Single(source => source.Id == sourceId));
    }

    private sealed class RecoveryFileSystem(IFileOperationFileSystem inner) : IFileOperationFileSystem
    {
        public Dictionary<string, FileAttributes> AttributeOverrides { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public bool Exists(string physicalPath) => inner.Exists(physicalPath);
        public void CreateDirectory(string physicalPath) => inner.CreateDirectory(physicalPath);
        public long GetFileLength(string physicalPath) => inner.GetFileLength(physicalPath);
        public Task<long> CopyFileAsync(string source, string destination, Func<long, CancellationToken, ValueTask> onBytes, CancellationToken cancellationToken) =>
            inner.CopyFileAsync(source, destination, onBytes, cancellationToken);
        public MoveAttempt TryMove(string source, string destination) => inner.TryMove(source, destination);
        public void DeleteFile(string physicalPath) => inner.DeleteFile(physicalPath);
        public void DeleteDirectory(string physicalPath, bool recursive) => inner.DeleteDirectory(physicalPath, recursive);
        public void ApplyBasicMetadata(string source, string destination) => inner.ApplyBasicMetadata(source, destination);
        public long? GetAvailableBytes(string physicalDirectory) => inner.GetAvailableBytes(physicalDirectory);
        public FileAttributes GetAttributes(string physicalPath) =>
            AttributeOverrides.TryGetValue(Path.GetFullPath(physicalPath), out var attributes)
                ? attributes
                : inner.GetAttributes(physicalPath);
    }
}

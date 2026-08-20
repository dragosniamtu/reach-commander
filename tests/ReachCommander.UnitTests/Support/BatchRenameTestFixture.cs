using ReachCommander.Application.BatchRenames;
using ReachCommander.Application.Sources;
using ReachCommander.Domain.Files;
using ReachCommander.Domain.Sources;
using ReachCommander.Infrastructure.BatchRenames;
using ReachCommander.Infrastructure.Mutations;
using ReachCommander.Infrastructure.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace ReachCommander.UnitTests.Support;

internal sealed class BatchRenameTestFixture : IDisposable
{
    private readonly TemporaryDirectory _temporary = new();
    private readonly TestBatchRenameFileSystem _fileSystem = new();
    private readonly SourceDefinition _source;
    private readonly PathSecurityService _paths;

    public BatchRenameTestFixture()
    {
        SourceRoot = _temporary.CreateDirectory("media");
        Directory.CreateDirectory(Path.Combine(SourceRoot, "Movies"));
        _source = new SourceDefinition(
            "media",
            "Media",
            SourceRoot,
            IsReadOnly: false,
            DefaultLeft: true,
            DefaultRight: false);
        _paths = new PathSecurityService(new FakeSourceCatalog(_source));
        Clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 20, 8, 0, 0, TimeSpan.Zero));
        PlanStore = new BatchRenamePlanStore(Clock);
    }

    public string SourceRoot { get; }

    public ManualTimeProvider Clock { get; }

    public BatchRenamePlanStore PlanStore { get; }

    public BatchRenamePlanner CreatePlanner(bool sourceReadOnly = false, int maxEntries = 5_000)
    {
        var source = _source with { IsReadOnly = sourceReadOnly };
        var paths = new PathSecurityService(new FakeSourceCatalog(source));
        return CreatePlanner(paths, _fileSystem, maxEntries);
    }

    public BatchRenameExecutor CreateExecutor(
        IBatchRenameFileSystem? fileSystem = null,
        DirectoryMutationLock? mutationLock = null)
    {
        var effectiveFileSystem = fileSystem ?? _fileSystem;
        return new BatchRenameExecutor(
            CreatePlanner(_paths, effectiveFileSystem, 5_000),
            _paths,
            effectiveFileSystem,
            mutationLock ?? new DirectoryMutationLock(),
            NullLogger<BatchRenameExecutor>.Instance);
    }

    public BatchRenameService CreateService()
    {
        var planner = CreatePlanner(_paths, _fileSystem, 5_000);
        var executor = new BatchRenameExecutor(
            planner,
            _paths,
            _fileSystem,
            new DirectoryMutationLock(),
            NullLogger<BatchRenameExecutor>.Instance);
        return new BatchRenameService(
            planner,
            PlanStore,
            executor,
            new BatchRenameRequestLock(),
            Clock);
    }

    public IBatchRenameFileSystem CreateFailingFileSystem(params int[] failOnMoveNumbers) =>
        new FailingBatchRenameFileSystem(_fileSystem, failOnMoveNumbers);

    public StoredBatchRenamePlan StoredPlan(
        string directory,
        params (string OldName, string NewName)[] renames)
    {
        var directoryPhysical = Path.Combine(
            SourceRoot,
            directory.Trim('/').Replace('/', Path.DirectorySeparatorChar));
        var entries = renames.Select(rename =>
        {
            var oldLogical = JoinLogicalPath(directory, rename.OldName);
            var newLogical = JoinLogicalPath(directory, rename.NewName);
            var oldPhysical = Path.Combine(directoryPhysical, rename.OldName);
            var newPhysical = Path.Combine(directoryPhysical, rename.NewName);
            var snapshot = _fileSystem.GetEntry(oldLogical, oldPhysical);
            return new PlannedRename(
                oldLogical,
                newLogical,
                oldPhysical,
                newPhysical,
                rename.OldName,
                rename.NewName,
                snapshot.Type,
                snapshot.Fingerprint,
                BatchRenamePreviewStatus.Ready,
                Message: null);
        }).ToArray();
        var now = Clock.GetUtcNow();
        var planId = Guid.NewGuid();
        var preview = new BatchRenamePreview(
            planId,
            now.AddMinutes(10),
            entries.Select(entry => new BatchRenamePreviewRow(
                entry.OldLogicalPath,
                entry.OldName,
                Path.GetExtension(entry.OldName).TrimStart('.') is { Length: > 0 } extension
                    ? extension
                    : null,
                entry.NewName,
                entry.Type,
                entry.PreviewFingerprint.Length,
                entry.PreviewFingerprint.ModifiedAt,
                entry.Status,
                entry.Message)).ToArray(),
            CanExecute: entries.Length > 0,
            ChangedCount: entries.Length,
            UnchangedCount: 0,
            InvalidCount: 0);
        return new StoredBatchRenamePlan(
            planId,
            now,
            preview.ExpiresAt,
            _source.Id,
            directory,
            directoryPhysical,
            entries,
            preview);
    }

    public string ReadFile(string relativePath) =>
        File.ReadAllText(Path.Combine(SourceRoot, relativePath));

    public bool EntryExists(string relativePath)
    {
        var path = Path.Combine(SourceRoot, relativePath);
        return File.Exists(path) || Directory.Exists(path);
    }

    public IReadOnlyList<string> ReservedTemporaryEntries(string relativeDirectory) =>
        Directory.EnumerateFileSystemEntries(Path.Combine(SourceRoot, relativeDirectory))
            .Select(Path.GetFileName)
            .Where(name => name is not null && name.StartsWith(
                ".reachcommander-rename-",
                StringComparison.Ordinal))
            .Cast<string>()
            .ToArray();

    private BatchRenamePlanner CreatePlanner(
        PathSecurityService paths,
        IBatchRenameFileSystem fileSystem,
        int maxEntries) => new(
            paths,
            fileSystem,
            new RenameRuleEvaluator(),
            new RenameNameValidator(),
            PlanStore,
            Clock,
            maxEntries);

    public void WriteFile(string relativePath, string content)
    {
        var path = Path.Combine(SourceRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void CreateDirectory(string relativePath) =>
        Directory.CreateDirectory(Path.Combine(SourceRoot, relativePath));

    public void MarkEntryAsSymbolicLink(string relativePath) =>
        _fileSystem.MarkAsSymbolicLink(ToLogicalPath(relativePath));

    public BatchRenamePreviewCommand Command(
        string directory,
        IReadOnlyList<string> children,
        BatchRenameRules? rules = null)
    {
        var paths = children.Select(child => child.StartsWith('/')
            ? child
            : directory == "/" ? $"/{child}" : $"{directory}/{child}").ToArray();
        return new BatchRenamePreviewCommand(
            "media",
            directory,
            paths,
            rules ?? Rules());
    }

    public BatchRenameRules Rules(
        string nameMask = "[N]",
        string extensionMask = "[E]",
        int counterDigits = 1) => new(
            nameMask,
            extensionMask,
            SearchFor: string.Empty,
            ReplaceWith: string.Empty,
            UseRegex: false,
            MatchCase: true,
            ReplaceInExtension: false,
            BatchRenameCaseMode.Unchanged,
            CounterStart: 1,
            CounterStep: 1,
            counterDigits);

    public void Dispose() => _temporary.Dispose();

    private static string ToLogicalPath(string relativePath) =>
        $"/{relativePath.Replace('\\', '/')}";

    private static string JoinLogicalPath(string directory, string name) =>
        directory == "/" ? $"/{name}" : $"{directory}/{name}";

    internal sealed class ManualTimeProvider(DateTimeOffset current) : TimeProvider
    {
        private DateTimeOffset _current = current;

        public override DateTimeOffset GetUtcNow() => _current;

        public void Advance(TimeSpan amount) => _current += amount;
    }

    private sealed class TestBatchRenameFileSystem : IBatchRenameFileSystem
    {
        private readonly LocalBatchRenameFileSystem _inner = new();
        private readonly HashSet<string> _symbolicLinks = new(StringComparer.Ordinal);

        public void MarkAsSymbolicLink(string logicalPath) => _symbolicLinks.Add(logicalPath);

        public BatchRenameEntrySnapshot GetEntry(string logicalPath, string physicalPath)
        {
            var entry = _inner.GetEntry(logicalPath, physicalPath);
            return _symbolicLinks.Contains(logicalPath)
                ? entry with { IsSymbolicLink = true }
                : entry;
        }

        public IReadOnlyList<BatchRenameEntrySnapshot> ListChildren(
            string parentLogicalPath,
            string parentPhysicalPath) =>
            _inner.ListChildren(parentLogicalPath, parentPhysicalPath)
                .Select(entry => _symbolicLinks.Contains(entry.LogicalPath)
                    ? entry with { IsSymbolicLink = true }
                    : entry)
                .ToArray();

        public bool EntryExists(string physicalPath) => _inner.EntryExists(physicalPath);

        public void Move(string sourcePhysicalPath, string destinationPhysicalPath, FileEntryType type) =>
            _inner.Move(sourcePhysicalPath, destinationPhysicalPath, type);
    }

    private sealed class FailingBatchRenameFileSystem(
        IBatchRenameFileSystem inner,
        IReadOnlyCollection<int> failOnMoveNumbers) : IBatchRenameFileSystem
    {
        private int _moveCount;

        public BatchRenameEntrySnapshot GetEntry(string logicalPath, string physicalPath) =>
            inner.GetEntry(logicalPath, physicalPath);

        public IReadOnlyList<BatchRenameEntrySnapshot> ListChildren(
            string parentLogicalPath,
            string parentPhysicalPath) => inner.ListChildren(parentLogicalPath, parentPhysicalPath);

        public bool EntryExists(string physicalPath) => inner.EntryExists(physicalPath);

        public void Move(string sourcePhysicalPath, string destinationPhysicalPath, FileEntryType type)
        {
            var moveNumber = Interlocked.Increment(ref _moveCount);
            if (failOnMoveNumbers.Contains(moveNumber))
            {
                throw new IOException("Injected move failure.");
            }

            inner.Move(sourcePhysicalPath, destinationPhysicalPath, type);
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

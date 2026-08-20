using ReachCommander.Application.BatchRenames;
using ReachCommander.Application.Sources;
using ReachCommander.Domain.Files;
using ReachCommander.Domain.Sources;
using ReachCommander.Infrastructure.BatchRenames;
using ReachCommander.Infrastructure.Security;

namespace ReachCommander.UnitTests.Support;

internal sealed class BatchRenameTestFixture : IDisposable
{
    private readonly TemporaryDirectory _temporary = new();
    private readonly TestBatchRenameFileSystem _fileSystem = new();

    public BatchRenameTestFixture()
    {
        SourceRoot = _temporary.CreateDirectory("media");
        Directory.CreateDirectory(Path.Combine(SourceRoot, "Movies"));
        Clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 20, 8, 0, 0, TimeSpan.Zero));
        PlanStore = new BatchRenamePlanStore(Clock);
    }

    public string SourceRoot { get; }

    public ManualTimeProvider Clock { get; }

    public BatchRenamePlanStore PlanStore { get; }

    public BatchRenamePlanner CreatePlanner(bool sourceReadOnly = false, int maxEntries = 5_000)
    {
        var source = new SourceDefinition(
            "media",
            "Media",
            SourceRoot,
            sourceReadOnly,
            DefaultLeft: true,
            DefaultRight: false);
        var paths = new PathSecurityService(new FakeSourceCatalog(source));
        return new BatchRenamePlanner(
            paths,
            _fileSystem,
            new RenameRuleEvaluator(),
            new RenameNameValidator(),
            PlanStore,
            Clock,
            maxEntries);
    }

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

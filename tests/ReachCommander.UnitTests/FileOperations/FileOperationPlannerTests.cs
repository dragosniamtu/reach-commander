using ReachCommander.Application.FileOperations;
using ReachCommander.Application.Sources;
using ReachCommander.Domain.Files;
using ReachCommander.Domain.Sources;
using ReachCommander.Infrastructure.FileOperations.Planning;

namespace ReachCommander.UnitTests.FileOperations;

public sealed class FileOperationPlannerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-25T10:00:00Z");
    private readonly ManualTimeProvider _clock = new(Now);
    private readonly FakeInspector _inspector = new();
    private readonly FakePlanStore _store;
    private readonly FileOperationPlanner _planner;

    public FileOperationPlannerTests()
    {
        _store = new FakePlanStore();
        _planner = new FileOperationPlanner(
            new FakeSourceCatalog(
                Source("media", readOnly: true),
                Source("downloads", readOnly: false)),
            _inspector,
            _store,
            _clock);
        _inspector.AddDirectory("media", "/");
        _inspector.AddDirectory("downloads", "/");
    }

    [Fact]
    public async Task Preview_copy_directory_reports_top_level_and_child_conflicts()
    {
        _inspector.AddDirectory("media", "/Shows");
        _inspector.AddFile("media", "/Shows/Episode.mkv", 12);
        _inspector.AddDirectory("downloads", "/Shows");
        _inspector.AddFile("downloads", "/Shows/Episode.mkv", 7);
        _inspector.AddFile("downloads", "/Shows/Poster.jpg", 3);

        var preview = await _planner.PreviewAsync(
            new(FileOperationKind.Copy, "media", ["/Shows"], "downloads", "/"),
            CancellationToken.None);

        Assert.Equal(2, preview.Conflicts.Count);
        Assert.Contains(preview.Conflicts, conflict =>
            conflict.DestinationLogicalPath == "/Shows" &&
            conflict.SourceType == FileEntryType.Directory &&
            conflict.DestinationType == FileEntryType.Directory);
        Assert.Contains(preview.Conflicts, conflict =>
            conflict.DestinationLogicalPath == "/Shows/Episode.mkv");
        Assert.DoesNotContain(preview.Conflicts, conflict =>
            conflict.DestinationLogicalPath == "/Shows/Poster.jpg");
        Assert.Equal(2, preview.TotalItems);
        Assert.Equal(12, preview.TotalBytes);
    }

    [Fact]
    public async Task Preview_copy_allows_read_only_source_and_preserves_input_order()
    {
        _inspector.AddFile("media", "/b.txt", 2);
        _inspector.AddFile("media", "/a.txt", 1);

        var preview = await _planner.PreviewAsync(
            new(FileOperationKind.Copy, "media", ["/b.txt", "/a.txt"], "downloads", "/"),
            CancellationToken.None);

        Assert.Equal(["/b.txt", "/a.txt"], preview.LogicalPaths);
        var plan = await _planner.GetValidatedPlanAsync(preview.PlanId, default);
        Assert.Equal(["/b.txt", "/a.txt"], plan.Entries.Select(entry => entry.SourceLogicalPath));
    }

    [Fact]
    public async Task Preview_move_rejects_read_only_source()
    {
        _inspector.AddFile("media", "/movie.mkv", 12);

        await Assert.ThrowsAsync<OperationSourceReadOnlyException>(() =>
            _planner.PreviewAsync(
                new(FileOperationKind.Move, "media", ["/movie.mkv"], "downloads", "/"),
                default));
    }

    [Theory]
    [InlineData("/Shows", "/Shows/Episode.mkv")]
    [InlineData("/Shows/Episode.mkv", "/Shows/Episode.mkv")]
    public async Task Preview_rejects_nested_or_duplicate_selection(string first, string second)
    {
        _inspector.AddDirectory("media", "/Shows");
        _inspector.AddFile("media", "/Shows/Episode.mkv", 12);

        await Assert.ThrowsAsync<InvalidOperationSelectionException>(() =>
            _planner.PreviewAsync(
                new(FileOperationKind.Copy, "media", [first, second], "downloads", "/"),
                default));
    }

    [Fact]
    public async Task Preview_rejects_directory_destination_inside_itself()
    {
        var planner = new FileOperationPlanner(
            new FakeSourceCatalog(Source("downloads", readOnly: false)),
            _inspector,
            _store,
            _clock);
        _inspector.AddDirectory("downloads", "/Shows");
        _inspector.AddDirectory("downloads", "/Shows/Season 1");

        await Assert.ThrowsAsync<InvalidOperationSelectionException>(() =>
            planner.PreviewAsync(
                new(FileOperationKind.Copy, "downloads", ["/Shows"], "downloads", "/Shows/Season 1"),
                default));
    }

    [Fact]
    public async Task Preview_rejects_symbolic_link_encountered_recursively()
    {
        _inspector.AddDirectory("media", "/Shows");
        _inspector.AddFile("media", "/Shows/linked.mkv", 12, isSymbolicLink: true);

        await Assert.ThrowsAsync<UnsafeSymbolicLinkException>(() =>
            _planner.PreviewAsync(
                new(FileOperationKind.Copy, "media", ["/Shows"], "downloads", "/"),
                default));
    }

    [Fact]
    public async Task Preview_rejects_known_insufficient_destination_capacity()
    {
        _inspector.AvailableBytes = 11;
        _inspector.AddFile("media", "/movie.mkv", 12);

        await Assert.ThrowsAsync<InsufficientStorageException>(() =>
            _planner.PreviewAsync(
                new(FileOperationKind.Copy, "media", ["/movie.mkv"], "downloads", "/"),
                default));
    }

    [Fact]
    public async Task GetValidatedPlanAsync_expires_after_ten_minutes()
    {
        _inspector.AddFile("media", "/movie.mkv", 12);
        var preview = await _planner.PreviewAsync(
            new(FileOperationKind.Copy, "media", ["/movie.mkv"], "downloads", "/"),
            default);

        _clock.Advance(TimeSpan.FromMinutes(10));

        await Assert.ThrowsAsync<OperationPlanExpiredException>(() =>
            _planner.GetValidatedPlanAsync(preview.PlanId, default));
    }

    private static SourceDefinition Source(string id, bool readOnly) =>
        new(id, id, $"X:/{id}", readOnly, false, false);

    private sealed class FakeSourceCatalog(params SourceDefinition[] sources) : ISourceCatalog
    {
        public ValueTask<IReadOnlyList<SourceDefinition>> GetDefinitionsAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<SourceDefinition>>(sources);

        public ValueTask<IReadOnlyList<SourceSnapshot>> GetSnapshotsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<SourceDefinition> GetRequiredAsync(string sourceId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(sources.Single(source => source.Id == sourceId));
    }

    private sealed class FakeInspector : IFileOperationInspector
    {
        private readonly Dictionary<(string SourceId, string Path), FileOperationEntrySnapshot> _entries = new();

        public long? AvailableBytes { get; set; } = long.MaxValue;

        public void AddDirectory(string sourceId, string path, bool isSymbolicLink = false) =>
            Add(sourceId, path, FileEntryType.Directory, null, isSymbolicLink);

        public void AddFile(string sourceId, string path, long length, bool isSymbolicLink = false) =>
            Add(sourceId, path, FileEntryType.File, length, isSymbolicLink);

        public ValueTask<FileOperationEntrySnapshot> GetRequiredAsync(
            string sourceId,
            string logicalPath,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(_entries[(sourceId, logicalPath)]);

        public ValueTask<FileOperationEntrySnapshot?> TryGetAsync(
            string sourceId,
            string logicalPath,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(_entries.GetValueOrDefault((sourceId, logicalPath)));

        public ValueTask<IReadOnlyList<FileOperationEntrySnapshot>> ListChildrenAsync(
            string sourceId,
            string logicalDirectory,
            CancellationToken cancellationToken)
        {
            var prefix = logicalDirectory == "/" ? "/" : $"{logicalDirectory}/";
            var children = _entries.Values
                .Where(entry => entry.SourceId == sourceId &&
                    entry.LogicalPath.StartsWith(prefix, StringComparison.Ordinal) &&
                    !entry.LogicalPath[prefix.Length..].Contains('/'))
                .OrderBy(entry => entry.Name, StringComparer.Ordinal)
                .ToArray();
            return ValueTask.FromResult<IReadOnlyList<FileOperationEntrySnapshot>>(children);
        }

        public ValueTask<long?> GetAvailableBytesAsync(
            string sourceId,
            string logicalDirectory,
            CancellationToken cancellationToken) => ValueTask.FromResult(AvailableBytes);

        private void Add(
            string sourceId,
            string path,
            FileEntryType type,
            long? length,
            bool isSymbolicLink)
        {
            var name = path == "/" ? "/" : path[(path.LastIndexOf('/') + 1)..];
            var fingerprint = new FileOperationEntryFingerprint(
                type,
                length,
                Now,
                type == FileEntryType.Directory ? FileAttributes.Directory : FileAttributes.Normal,
                isSymbolicLink);
            _entries[(sourceId, path)] = new(sourceId, path, name, fingerprint);
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset current) : TimeProvider
    {
        private DateTimeOffset _current = current;

        public override DateTimeOffset GetUtcNow() => _current;

        public void Advance(TimeSpan duration) => _current = _current.Add(duration);
    }

    private sealed class FakePlanStore : IFileOperationPlanStore
    {
        private readonly Dictionary<Guid, FileOperationPlan> _plans = new();

        public ValueTask SaveAsync(FileOperationPlan plan, CancellationToken cancellationToken)
        {
            _plans[plan.PlanId] = plan;
            return ValueTask.CompletedTask;
        }

        public ValueTask<FileOperationPlan?> GetAsync(Guid planId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(_plans.GetValueOrDefault(planId));

        public ValueTask DeleteAsync(Guid planId, CancellationToken cancellationToken)
        {
            _plans.Remove(planId);
            return ValueTask.CompletedTask;
        }
    }
}

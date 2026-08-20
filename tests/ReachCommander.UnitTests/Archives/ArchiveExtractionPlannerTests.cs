using Microsoft.Extensions.Options;
using ReachCommander.Application.Archives;
using ReachCommander.Application.Files;
using ReachCommander.Application.Sources;
using ReachCommander.Domain.Archives;
using ReachCommander.Domain.Sources;
using ReachCommander.Infrastructure.Archives;
using ReachCommander.Infrastructure.Archives.Catalog;
using ReachCommander.Infrastructure.Archives.Extraction;
using ReachCommander.Infrastructure.Archives.Volumes;

namespace ReachCommander.UnitTests.Archives;

public sealed class ArchiveExtractionPlannerTests
{
    [Fact]
    public async Task Selected_file_is_relative_to_the_current_archive_directory()
    {
        var fixture = CreateFixture([
            Entry(1, "Family/2025/photo.txt", size: 12, compressedSize: 6),
        ]);

        var preview = await fixture.Planner.PreviewAsync(
            Request("/Family/2025", ["/Family/2025/photo.txt"]),
            CancellationToken.None);
        var plan = fixture.Store.GetRequiredPlan(preview.PlanId);

        Assert.Equal(["photo.txt"], preview.SelectedRoots);
        var file = Assert.Single(plan.Files);
        Assert.Equal(1, file.WorkerEntryIndex);
        Assert.Equal("/Family/2025/photo.txt", file.ArchivePath);
        Assert.Equal("photo.txt", file.RelativeOutputPath);
        Assert.Equal(12, preview.TotalExtractedBytes);
        Assert.True(preview.CanExecute);
    }

    [Fact]
    public async Task Selected_directory_keeps_descendants_and_removes_redundant_child_selection()
    {
        var fixture = CreateFixture([
            Entry(1, "Family/one.txt"),
            Entry(2, "Family/Child/two.txt"),
            Entry(3, "other.txt"),
        ]);

        var preview = await fixture.Planner.PreviewAsync(
            Request("/", ["/Family", "/Family/Child/two.txt"]),
            CancellationToken.None);
        var plan = fixture.Store.GetRequiredPlan(preview.PlanId);

        Assert.Equal(["Family"], preview.SelectedRoots);
        Assert.Equal(["Family", "Family/Child"], plan.Directories);
        Assert.Equal(
            ["Family/Child/two.txt", "Family/one.txt"],
            plan.Files.Select(file => file.RelativeOutputPath));
    }

    [Fact]
    public async Task Extract_all_places_root_contents_directly_without_an_archive_wrapper()
    {
        var fixture = CreateFixture([
            Entry(1, "Family/photo.txt"),
            Entry(2, "root.txt"),
        ]);

        var preview = await fixture.Planner.PreviewAsync(
            Request("/", [], extractAll: true),
            CancellationToken.None);
        var plan = fixture.Store.GetRequiredPlan(preview.PlanId);

        Assert.Equal(["Family", "root.txt"], preview.SelectedRoots);
        Assert.Equal(["Family/photo.txt", "root.txt"], plan.Files.Select(file => file.RelativeOutputPath));
        Assert.DoesNotContain(plan.Files, file => file.RelativeOutputPath.StartsWith("photos", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Conflicts_are_case_insensitive_complete_and_non_executable()
    {
        var fixture = CreateFixture([
            Entry(1, "Family/photo.txt"),
            Entry(2, "root.txt"),
        ]);
        fixture.FileSystem.Children =
        [
            new("family", true, null, DateTimeOffset.Parse("2026-08-20T08:00:00Z")),
            new("ROOT.TXT", false, 9, DateTimeOffset.Parse("2026-08-20T08:00:00Z")),
        ];

        var preview = await fixture.Planner.PreviewAsync(
            Request("/", [], extractAll: true),
            CancellationToken.None);

        Assert.False(preview.CanExecute);
        var conflict = Assert.Single(preview.Conflicts);
        Assert.Equal("archive_destination_conflict", conflict.Code);
        Assert.Equal(["Family", "root.txt"], conflict.LogicalPaths);
        Assert.Empty(preview.Violations);
        Assert.Throws<ArchiveDestinationConflictException>(() =>
            fixture.Store.BindOperation(preview.PlanId, "operation"));
    }

    [Fact]
    public async Task Known_size_above_free_space_is_a_violation_but_unknown_size_remains_allowed()
    {
        var known = CreateFixture([Entry(1, "large.bin", size: 11, compressedSize: 1)]);
        known.FileSystem.AvailableFreeSpace = 10;
        var unknown = CreateFixture([Entry(1, "unknown.bin", size: null, compressedSize: null)]);
        unknown.FileSystem.AvailableFreeSpace = 0;

        var knownPreview = await known.Planner.PreviewAsync(
            Request("/", [], extractAll: true),
            CancellationToken.None);
        var unknownPreview = await unknown.Planner.PreviewAsync(
            Request("/", [], extractAll: true),
            CancellationToken.None);

        Assert.False(knownPreview.CanExecute);
        Assert.Contains(knownPreview.Violations, issue => issue.Code == "archive_limit_exceeded");
        Assert.Null(unknownPreview.TotalExtractedBytes);
        Assert.True(unknownPreview.CanExecute);
    }

    [Fact]
    public async Task Destination_must_be_available_and_writable()
    {
        var unavailable = CreateFixture([Entry(1, "one.txt")]);
        unavailable.Sources.Destination = unavailable.Sources.Destination with { IsAvailable = false };
        var readOnly = CreateFixture([Entry(1, "one.txt")]);
        readOnly.Sources.Destination = readOnly.Sources.Destination with { IsReadOnly = true };

        await Assert.ThrowsAsync<ArchiveDestinationInvalidException>(() =>
            unavailable.Planner.PreviewAsync(Request("/", [], extractAll: true), CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArchiveDestinationReadOnlyException>(() =>
            readOnly.Planner.PreviewAsync(Request("/", [], extractAll: true), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Output_length_limit_returns_a_safe_non_executable_preview()
    {
        var options = new ArchiveOptions { MaxPathCharacters = 24 };
        var fixture = CreateFixture([Entry(1, "long-output-name.txt")], options);

        var preview = await fixture.Planner.PreviewAsync(
            Request("/", [], extractAll: true, destinationPath: "/already-long"),
            CancellationToken.None);

        Assert.False(preview.CanExecute);
        Assert.Contains(preview.Violations, issue => issue.Code == "archive_limit_exceeded");
        Assert.DoesNotContain("physical", System.Text.Json.JsonSerializer.Serialize(preview), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Output_depth_limit_includes_the_destination_directory()
    {
        var options = new ArchiveOptions { MaxPathDepth = 2 };
        var fixture = CreateFixture([Entry(1, "file.txt")], options);

        var preview = await fixture.Planner.PreviewAsync(
            Request("/", [], extractAll: true, destinationPath: "/already/deep"),
            CancellationToken.None);

        Assert.False(preview.CanExecute);
        Assert.Contains(preview.Violations, issue => issue.Code == "archive_limit_exceeded");
    }

    [Fact]
    public async Task Every_nested_output_is_validated_through_path_security()
    {
        var fixture = CreateFixture([
            Entry(1, "Family/photo.txt"),
            Entry(2, "Family/Albums/cover.jpg"),
        ]);

        await fixture.Planner.PreviewAsync(
            Request("/", [], extractAll: true),
            CancellationToken.None);

        Assert.Equal(
            [
                "/Extracted/Family",
                "/Extracted/Family/Albums",
                "/Extracted/Family/Albums/cover.jpg",
                "/Extracted/Family/photo.txt",
            ],
            fixture.PathSecurity.DescendantRequests.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Plan_id_is_256_bit_base64url_and_expiration_is_exactly_ten_minutes()
    {
        var fixture = CreateFixture([Entry(1, "one.txt")], idGenerator: new ArchivePlanIdGenerator());

        var preview = await fixture.Planner.PreviewAsync(
            Request("/", [], extractAll: true),
            CancellationToken.None);

        Assert.Equal(43, preview.PlanId.Length);
        Assert.DoesNotContain('=', preview.PlanId);
        Assert.DoesNotContain('+', preview.PlanId);
        Assert.DoesNotContain('/', preview.PlanId);
        Assert.Equal(fixture.Clock.GetUtcNow().AddMinutes(10), preview.ExpiresAt);
    }

    [Fact]
    public async Task Invalid_selection_becomes_a_non_executable_preview_and_secondary_failure_stays_structural()
    {
        var fixture = CreateFixture([Entry(1, "one.txt")]);
        var preview = await fixture.Planner.PreviewAsync(
            Request("/", ["/missing.txt"]),
            CancellationToken.None);
        fixture.CatalogProvider.Failure = new ArchiveVolumeSecondaryException("/photos.7z.001");

        Assert.False(preview.CanExecute);
        Assert.Contains(preview.Violations, issue => issue.Code == "archive_entry_unsafe");
        await Assert.ThrowsAsync<ArchiveVolumeSecondaryException>(() =>
            fixture.Planner.PreviewAsync(Request("/", [], extractAll: true), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Store_expires_unbound_plans_and_binds_execution_idempotently()
    {
        var fixture = CreateFixture([Entry(1, "one.txt")]);
        var preview = await fixture.Planner.PreviewAsync(
            Request("/", [], extractAll: true), CancellationToken.None);

        Assert.Equal("operation-one", fixture.Store.BindOperation(preview.PlanId, "operation-one"));
        Assert.Equal("operation-one", fixture.Store.BindOperation(preview.PlanId, "operation-two"));

        var expiring = CreateFixture([Entry(1, "one.txt")]);
        var expiringPreview = await expiring.Planner.PreviewAsync(
            Request("/", [], extractAll: true), CancellationToken.None);
        expiring.Clock.Advance(TimeSpan.FromMinutes(10));

        Assert.Throws<ArchivePlanExpiredException>(() => expiring.Store.GetRequiredPlan(expiringPreview.PlanId));
    }

    [Fact]
    public async Task Bound_plan_survives_expiration_until_a_safe_release()
    {
        var fixture = CreateFixture([Entry(1, "one.txt")]);
        var preview = await fixture.Planner.PreviewAsync(
            Request("/", [], extractAll: true), CancellationToken.None);
        var plan = fixture.Store.GetRequiredPlan(preview.PlanId);
        fixture.Store.BindOperation(preview.PlanId, "operation");
        fixture.Operations.Create("operation", plan);
        fixture.Store.CommitBinding(preview.PlanId, "operation");
        fixture.Clock.Advance(TimeSpan.FromMinutes(10));

        Assert.Equal(preview.PlanId, fixture.Store.GetRequiredPlan(preview.PlanId).PlanId);
        Assert.True(fixture.Store.ReleaseBinding(preview.PlanId, "operation"));
        Assert.Throws<ArchivePlanNotFoundException>(() => fixture.Store.GetRequiredPlan(preview.PlanId));
    }

    [Fact]
    public async Task Pending_operation_binding_survives_expiry_cleanup_until_registration()
    {
        var fixture = CreateFixture([Entry(1, "one.txt")]);
        var preview = await fixture.Planner.PreviewAsync(
            Request("/", [], extractAll: true), CancellationToken.None);
        fixture.Store.BindOperation(preview.PlanId, "pending-operation");
        fixture.Clock.Advance(TimeSpan.FromMinutes(10));

        _ = await fixture.Planner.PreviewAsync(
            Request("/", [], extractAll: true), CancellationToken.None);

        var reservedPlan = fixture.Store.GetRequiredPlan(preview.PlanId);
        fixture.Operations.Create("pending-operation", reservedPlan);
        fixture.Store.CommitBinding(preview.PlanId, "pending-operation");

        Assert.Equal(preview.PlanId, fixture.Store.GetRequiredPlan(preview.PlanId).PlanId);
    }

    [Fact]
    public async Task Bound_plan_is_reclaimed_only_after_plan_and_operation_retention_expire()
    {
        var fixture = CreateFixture([Entry(1, "one.txt")]);
        var preview = await fixture.Planner.PreviewAsync(
            Request("/", [], extractAll: true), CancellationToken.None);
        var plan = fixture.Store.GetRequiredPlan(preview.PlanId);
        fixture.Store.BindOperation(preview.PlanId, "operation");
        fixture.Operations.Create("operation", plan);
        fixture.Store.CommitBinding(preview.PlanId, "operation");
        fixture.Operations.MarkCompleted("operation");

        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(preview.PlanId, fixture.Store.GetRequiredPlan(preview.PlanId).PlanId);

        fixture.Clock.Advance(TimeSpan.FromMinutes(51));
        Assert.Throws<ArchivePlanNotFoundException>(() =>
            fixture.Store.GetRequiredPlan(preview.PlanId));
    }

    [Fact]
    public async Task Terminal_operation_cap_does_not_break_idempotency_while_plan_is_valid()
    {
        var fixture = CreateFixture([Entry(1, "one.txt")], idGenerator: new ArchivePlanIdGenerator());
        var bindings = new List<(string PlanId, string OperationId)>();
        for (var index = 0; index < 101; index++)
        {
            var preview = await fixture.Planner.PreviewAsync(
                Request("/", [], extractAll: true), CancellationToken.None);
            var operationId = $"operation-{index}";
            var plan = fixture.Store.GetRequiredPlan(preview.PlanId);
            fixture.Store.BindOperation(preview.PlanId, operationId);
            fixture.Operations.Create(operationId, plan);
            fixture.Store.CommitBinding(preview.PlanId, operationId);
            fixture.Operations.MarkCompleted(operationId);
            bindings.Add((preview.PlanId, operationId));
        }

        var oldest = bindings[0];
        Assert.Equal(
            oldest.OperationId,
            fixture.Store.BindOperation(oldest.PlanId, "replacement-operation"));
        Assert.Equal(oldest.OperationId, fixture.Operations.GetRequired(oldest.OperationId).OperationId);

        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.False(fixture.Operations.Contains(oldest.OperationId));
        Assert.True(fixture.Operations.Contains(bindings[1].OperationId));
        Assert.True(fixture.Operations.Contains(bindings[^1].OperationId));
    }

    [Fact]
    public async Task Store_caps_unbound_plans_at_128_and_evicts_the_oldest()
    {
        var fixture = CreateFixture([Entry(1, "one.txt")]);
        var previews = new List<ArchiveExtractionPreview>();
        for (var index = 0; index < 129; index++)
        {
            previews.Add(await fixture.Planner.PreviewAsync(
                Request("/", [], extractAll: true), CancellationToken.None));
            fixture.Clock.Advance(TimeSpan.FromMilliseconds(1));
        }

        Assert.Throws<ArchivePlanNotFoundException>(() =>
            fixture.Store.GetRequiredPlan(previews[0].PlanId));
        Assert.Equal(previews[^1].PlanId, fixture.Store.GetRequiredPlan(previews[^1].PlanId).PlanId);
    }

    [Fact]
    public async Task Store_never_returns_a_plan_id_that_was_discarded_at_capacity()
    {
        var fixture = CreateFixture([Entry(1, "one.txt")]);
        for (var index = 0; index < 128; index++)
        {
            var preview = await fixture.Planner.PreviewAsync(
                Request("/", [], extractAll: true), CancellationToken.None);
            fixture.Store.BindOperation(preview.PlanId, $"operation-{index}");
        }

        await Assert.ThrowsAsync<ArchiveCapacityReachedException>(() =>
            fixture.Planner.PreviewAsync(
                Request("/", [], extractAll: true), CancellationToken.None).AsTask());
    }

    [Fact]
    public void Destination_snapshot_changes_with_name_type_length_or_timestamp()
    {
        var timestamp = DateTimeOffset.Parse("2026-08-20T08:00:00Z");
        var baseline = ArchiveExtractionPlanner.CreateDestinationSnapshot([
            new("one.txt", false, 1, timestamp),
        ]);

        Assert.NotEqual(baseline, ArchiveExtractionPlanner.CreateDestinationSnapshot([
            new("ONE.txt", true, 1, timestamp),
        ]));
        Assert.NotEqual(baseline, ArchiveExtractionPlanner.CreateDestinationSnapshot([
            new("one.txt", false, 2, timestamp),
        ]));
        Assert.NotEqual(baseline, ArchiveExtractionPlanner.CreateDestinationSnapshot([
            new("one.txt", false, 1, timestamp.AddSeconds(1)),
        ]));
    }

    [Fact]
    public void Linux_volume_selection_uses_the_longest_containing_mount()
    {
        Assert.Equal(
            "/mnt/storage",
            LocalArchiveExtractionFileSystem.FindContainingVolumeRoot(
                "/mnt/storage/Photos",
                ["/", "/mnt", "/mnt/storage", "/mnt/storage-old"],
                StringComparison.Ordinal));
        Assert.Equal(
            "/",
            LocalArchiveExtractionFileSystem.FindContainingVolumeRoot(
                "/mnt/storage-oldish/Photos",
                ["/", "/mnt/storage-old"],
                StringComparison.Ordinal));
    }

    [Fact]
    public void Windows_volume_selection_accepts_a_drive_root_with_its_separator()
    {
        Assert.Equal(
            "C:\\",
            LocalArchiveExtractionFileSystem.FindContainingVolumeRoot(
                "C:\\Media\\Photos",
                ["C:\\", "D:\\"],
                StringComparison.OrdinalIgnoreCase));
    }

    private static PlannerFixture CreateFixture(
        IReadOnlyList<UntrustedArchiveEntry> entries,
        ArchiveOptions? options = null,
        IArchivePlanIdGenerator? idGenerator = null)
    {
        options ??= new ArchiveOptions();
        var catalog = new ArchiveCatalogBuilder(Options.Create(options)).Build(ArchiveFormat.Zip, entries);
        var part = new ResolvedArchivePart(
            "/photos.zip",
            "C:/private/photos.zip",
            100,
            DateTimeOffset.Parse("2026-08-20T08:00:00Z"));
        var partSet = new ResolvedArchivePartSet(
            ArchiveFormat.Zip,
            "/photos.zip",
            [part],
            new ArchiveVolumeFingerprint("fingerprint"));
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-20T10:00:00Z"));
        var operations = new ArchiveExtractionOperationStore(clock);
        var store = new ArchiveExtractionPlanStore(clock, operations);
        var sources = new FakeSourceCatalog();
        var pathSecurity = new FakePathSecurity(sources);
        var fileSystem = new FakeArchiveExtractionFileSystem();
        var provider = new FakeCatalogProvider(new ResolvedArchiveCatalog(partSet, catalog));
        var planner = new ArchiveExtractionPlanner(
            provider,
            sources,
            pathSecurity,
            fileSystem,
            store,
            idGenerator ?? new FixedPlanIdGenerator(),
            Options.Create(options),
            clock);
        return new PlannerFixture(
            planner,
            store,
            operations,
            provider,
            sources,
            pathSecurity,
            fileSystem,
            clock);
    }

    private static ArchiveExtractionPreviewRequest Request(
        string internalDirectory,
        IReadOnlyList<string> entries,
        bool extractAll = false,
        string destinationPath = "/Extracted") =>
        new(
            "downloads",
            "/photos.zip",
            internalDirectory,
            entries,
            extractAll,
            "media",
            destinationPath);

    private static UntrustedArchiveEntry Entry(
        int index,
        string key,
        long? size = 1,
        long? compressedSize = 1) =>
        new(
            index,
            key,
            IsDirectory: false,
            IsEncrypted: false,
            IsLink: false,
            IsSpecial: false,
            size,
            compressedSize,
            DateTimeOffset.Parse("2026-08-20T09:00:00Z"));

    private sealed record PlannerFixture(
        ArchiveExtractionPlanner Planner,
        ArchiveExtractionPlanStore Store,
        ArchiveExtractionOperationStore Operations,
        FakeCatalogProvider CatalogProvider,
        FakeSourceCatalog Sources,
        FakePathSecurity PathSecurity,
        FakeArchiveExtractionFileSystem FileSystem,
        ManualTimeProvider Clock);

    private sealed class FakeCatalogProvider(ResolvedArchiveCatalog result) : IArchiveCatalogProvider
    {
        public Exception? Failure { get; set; }

        public ValueTask<ResolvedArchiveCatalog> GetAsync(
            string sourceId,
            string archivePath,
            CancellationToken cancellationToken) =>
            Failure is null
                ? ValueTask.FromResult(result)
                : ValueTask.FromException<ResolvedArchiveCatalog>(Failure);
    }

    private sealed class FakeSourceCatalog : ISourceCatalog
    {
        public SourceSnapshot Destination { get; set; } = new(
            "media", "Media", true, false, 1000, 100, 900, false, true);

        public ValueTask<IReadOnlyList<SourceDefinition>> GetDefinitionsAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<SourceDefinition>>([
                Definition("downloads", false), Definition("media", Destination.IsReadOnly),
            ]);

        public ValueTask<IReadOnlyList<SourceSnapshot>> GetSnapshotsAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<SourceSnapshot>>([
                new("downloads", "Downloads", true, false, 1000, 100, 900, true, false),
                Destination,
            ]);

        public ValueTask<SourceDefinition> GetRequiredAsync(
            string sourceId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Definition(sourceId, sourceId == "media" && Destination.IsReadOnly));

        private static SourceDefinition Definition(string id, bool readOnly) =>
            new(id, id, $"C:/private/{id}", readOnly, false, false);
    }

    private sealed class FakePathSecurity(FakeSourceCatalog sources) : IPathSecurityService
    {
        public List<string> DescendantRequests { get; } = [];

        public async ValueTask<ResolvedSourcePath> ResolveAsync(
            string sourceId,
            string logicalPath,
            CancellationToken cancellationToken) =>
            new(
                await sources.GetRequiredAsync(sourceId, cancellationToken),
                logicalPath,
                $"C:/private/{sourceId}{logicalPath}");

        public async ValueTask<ResolvedSourcePath> ResolveChildAsync(
            string sourceId,
            string parentLogicalPath,
            string childName,
            CancellationToken cancellationToken) =>
            new(
                await sources.GetRequiredAsync(sourceId, cancellationToken),
                parentLogicalPath == "/" ? $"/{childName}" : $"{parentLogicalPath}/{childName}",
                $"C:/private/{sourceId}/{childName}");

        public async ValueTask<ResolvedSourcePath> ResolveDescendantAsync(
            string sourceId,
            string parentLogicalPath,
            string relativePath,
            CancellationToken cancellationToken)
        {
            var logicalPath = parentLogicalPath == "/"
                ? $"/{relativePath}"
                : $"{parentLogicalPath}/{relativePath}";
            DescendantRequests.Add(logicalPath);
            return new(
                await sources.GetRequiredAsync(sourceId, cancellationToken),
                logicalPath,
                $"C:/private/{sourceId}/{relativePath}");
        }
    }

    private sealed class FakeArchiveExtractionFileSystem : IArchiveExtractionFileSystem
    {
        public IReadOnlyList<ArchiveDestinationEntry> Children { get; set; } = [];

        public long? AvailableFreeSpace { get; set; } = 900;

        public bool DirectoryExists(string physicalPath) => true;

        public IReadOnlyList<ArchiveDestinationEntry> ListChildren(string physicalDirectory) => Children;

        public long? GetAvailableFreeSpace(string physicalDirectory) => AvailableFreeSpace;
    }

    private sealed class FixedPlanIdGenerator : IArchivePlanIdGenerator
    {
        private int _next;

        public string CreateId() => $"fixed-plan-{Interlocked.Increment(ref _next)}";
    }

    private sealed class ManualTimeProvider(DateTimeOffset value) : TimeProvider
    {
        private DateTimeOffset _value = value;

        public override DateTimeOffset GetUtcNow() => _value;

        public void Advance(TimeSpan amount) => _value = _value.Add(amount);
    }
}

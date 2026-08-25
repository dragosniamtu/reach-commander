using ReachCommander.Application.FileOperations;
using ReachCommander.Domain.Files;
using ReachCommander.Infrastructure.FileOperations.Persistence;
using ReachCommander.Infrastructure.FileOperations.Planning;
using ReachCommander.Infrastructure.Mutations;
using ReachCommander.UnitTests.Support;

namespace ReachCommander.UnitTests.FileOperations;

public sealed class JsonFileOperationPlanStoreTests : IDisposable
{
    private readonly TemporaryDirectory _temporary = new();

    [Fact]
    public async Task SaveAsync_round_trips_logical_plan_without_host_root()
    {
        var paths = FileOperationDataPaths.FromAuthenticationRoot(_temporary.Path);
        var store = new JsonFileOperationPlanStore(paths);
        var plan = Plan();

        await store.SaveAsync(plan, default);
        var loaded = await store.GetAsync(plan.PlanId, default);
        var json = await File.ReadAllTextAsync(paths.PlanPath(plan.PlanId));

        Assert.NotNull(loaded);
        Assert.Equal(plan.PlanId, loaded.PlanId);
        Assert.Equal(plan.Kind, loaded.Kind);
        Assert.Equal(plan.SourceLogicalPaths, loaded.SourceLogicalPaths);
        Assert.Equal(plan.Entries, loaded.Entries);
        Assert.Equal(plan.LockTargets, loaded.LockTargets);
        Assert.DoesNotContain(_temporary.Path, json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetAsync_rejects_unknown_schema_version()
    {
        var paths = FileOperationDataPaths.FromAuthenticationRoot(_temporary.Path);
        paths.EnsureDirectories();
        var plan = Plan();
        await AtomicJsonFile.WriteAsync(
            paths.PlanPath(plan.PlanId),
            new PersistedFileOperationPlanDocument(99, plan),
            default);
        var store = new JsonFileOperationPlanStore(paths);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.GetAsync(plan.PlanId, default).AsTask());
    }

    public void Dispose() => _temporary.Dispose();

    private static FileOperationPlan Plan()
    {
        var now = DateTimeOffset.Parse("2026-08-25T10:00:00Z");
        var fingerprint = new FileOperationEntryFingerprint(
            FileEntryType.File,
            5,
            now,
            FileAttributes.Normal,
            false);
        return new FileOperationPlan(
            Guid.NewGuid(),
            now,
            now.AddMinutes(10),
            FileOperationKind.Copy,
            "media",
            ["/photo.jpg"],
            "downloads",
            "/",
            [new("/photo.jpg", "/photo.jpg", "/photo.jpg", fingerprint, null, null, true)],
            [],
            null,
            [],
            [new DirectoryMutationTarget("downloads", "/")],
            5);
    }
}

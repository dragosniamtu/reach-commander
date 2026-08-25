using ReachCommander.Infrastructure.FileOperations.Persistence;

namespace ReachCommander.Infrastructure.FileOperations.Planning;

internal sealed class JsonFileOperationPlanStore(FileOperationDataPaths paths)
    : IFileOperationPlanStore
{
    public async ValueTask SaveAsync(
        FileOperationPlan plan,
        CancellationToken cancellationToken)
    {
        paths.EnsureDirectories();
        await AtomicJsonFile.WriteAsync(
            paths.PlanPath(plan.PlanId),
            new PersistedFileOperationPlanDocument(FileOperationSchema.CurrentVersion, plan),
            cancellationToken);
    }

    public async ValueTask<FileOperationPlan?> GetAsync(
        Guid planId,
        CancellationToken cancellationToken)
    {
        var path = paths.PlanPath(planId);
        if (!File.Exists(path))
        {
            return null;
        }

        var document = await AtomicJsonFile.ReadAsync<PersistedFileOperationPlanDocument>(
            path,
            cancellationToken);
        if (document.SchemaVersion != FileOperationSchema.CurrentVersion ||
            document.Plan.PlanId != planId)
        {
            throw new InvalidDataException("The persisted operation plan schema is invalid.");
        }

        return document.Plan;
    }

    public ValueTask DeleteAsync(Guid planId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(paths.PlanPath(planId));
        return ValueTask.CompletedTask;
    }
}

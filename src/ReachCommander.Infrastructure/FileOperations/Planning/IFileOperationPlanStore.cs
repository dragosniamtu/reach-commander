namespace ReachCommander.Infrastructure.FileOperations.Planning;

internal interface IFileOperationPlanStore
{
    ValueTask SaveAsync(FileOperationPlan plan, CancellationToken cancellationToken);

    ValueTask<FileOperationPlan?> GetAsync(Guid planId, CancellationToken cancellationToken);

    ValueTask DeleteAsync(Guid planId, CancellationToken cancellationToken);
}

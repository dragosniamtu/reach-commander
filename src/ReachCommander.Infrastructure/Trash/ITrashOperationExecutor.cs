using ReachCommander.Application.FileOperations;
using ReachCommander.Infrastructure.FileOperations.Persistence;

namespace ReachCommander.Infrastructure.Trash;

internal interface ITrashOperationExecutor
{
    Task<FileOperationStatus> ExecuteAsync(
        PersistedFileOperationDocument claimed,
        CancellationToken cancellationToken);
}

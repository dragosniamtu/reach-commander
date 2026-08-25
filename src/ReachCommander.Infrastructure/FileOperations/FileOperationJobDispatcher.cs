using ReachCommander.Application.FileOperations;
using ReachCommander.Infrastructure.FileOperations.Execution;
using ReachCommander.Infrastructure.FileOperations.Persistence;
using ReachCommander.Infrastructure.Trash;

namespace ReachCommander.Infrastructure.FileOperations;

internal sealed class FileOperationJobDispatcher(
    FileOperationExecutor files,
    ITrashOperationExecutor trash)
{
    internal Task<FileOperationStatus> DispatchAsync(
        PersistedFileOperationDocument job,
        CancellationToken cancellationToken) =>
        job.Plan.Kind is FileOperationKind.Copy or FileOperationKind.Move
            ? files.ExecuteAsync(job, cancellationToken)
            : trash.ExecuteAsync(job, cancellationToken);
}

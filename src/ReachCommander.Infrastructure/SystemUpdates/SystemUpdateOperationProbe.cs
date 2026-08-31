using ReachCommander.Application.Archives;
using ReachCommander.Application.FileOperations;
using ReachCommander.Application.SourceManagement;
using ReachCommander.Infrastructure.Archives.Extraction;

namespace ReachCommander.Infrastructure.SystemUpdates;

internal interface ISystemUpdateOperationProbe
{
    Task<bool> HasActiveOperationsAsync(CancellationToken cancellationToken);
}

internal sealed class SystemUpdateOperationProbe(
    IFileOperationService fileOperations,
    ArchiveExtractionOperationStore? archiveOperations = null) :
    ISystemUpdateOperationProbe,
    ISourceManagementOperationEligibility
{
    public async Task<bool> HasActiveOperationsAsync(CancellationToken cancellationToken)
    {
        var operations = await fileOperations.ListAsync(cancellationToken).ConfigureAwait(false);
        if (operations.Any(operation => operation.Phase is
                FileOperationPhase.Queued or
                FileOperationPhase.Validating or
                FileOperationPhase.Running or
                FileOperationPhase.Cancelling))
        {
            return true;
        }

        return archiveOperations?.HasActiveOperations() == true;
    }
}

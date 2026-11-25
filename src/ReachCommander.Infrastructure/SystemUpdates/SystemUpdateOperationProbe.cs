using ReachCommander.Application.Archives;
using ReachCommander.Application.FileOperations;
using ReachCommander.Application.SourceManagement;
using ReachCommander.Infrastructure.Archives.Extraction;
using ReachCommander.Infrastructure.TextEncodings;
using ReachCommander.Application.SubtitleGeneration;

namespace ReachCommander.Infrastructure.SystemUpdates;

internal interface ISystemUpdateOperationProbe
{
    Task<bool> HasActiveOperationsAsync(CancellationToken cancellationToken);
}

internal sealed class SystemUpdateOperationProbe(
    IFileOperationService fileOperations,
    ArchiveExtractionOperationStore? archiveOperations = null,
    TextEncodingOperationStore? textEncodings = null,
    ISubtitleGenerationService? subtitleJobs = null) :
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

        if (subtitleJobs is not null && (await subtitleJobs.ListAsync(cancellationToken)).Any(job =>
                job.Phase is SubtitleJobPhase.Queued or SubtitleJobPhase.Discovering or SubtitleJobPhase.Running or SubtitleJobPhase.Cancelling))
            return true;

        return archiveOperations?.HasActiveOperations() == true ||
            textEncodings?.HasActiveOperations() == true;
    }
}

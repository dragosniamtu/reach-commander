using ReachCommander.Application.FileOperations;
using ReachCommander.Infrastructure.FileOperations.Planning;

namespace ReachCommander.Infrastructure.FileOperations.Persistence;

internal static class FileOperationSchema
{
    internal const int CurrentVersion = 1;
}

internal sealed record PersistedFileOperationPlanDocument(
    int SchemaVersion,
    FileOperationPlan Plan);

internal sealed record FileOperationSubmissionApproval(
    IReadOnlyList<FileOperationConflictResolution> Resolutions,
    bool PermanentDeleteConfirmed);

internal sealed record PersistedFileOperationDocument(
    int SchemaVersion,
    long Sequence,
    FileOperationPlan Plan,
    FileOperationSubmissionApproval Approval,
    FileOperationStatus Status,
    bool CancellationRequested,
    FileOperationExecutionJournal? Journal)
{
    internal Guid OperationId => Status.OperationId;
}

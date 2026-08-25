using ReachCommander.Application.FileOperations;
using ReachCommander.Infrastructure.FileOperations.Persistence;
using ReachCommander.Infrastructure.FileOperations.Planning;

namespace ReachCommander.Infrastructure.FileOperations;

internal sealed class FileOperationService(
    FileOperationPlanner planner,
    FileOperationRepository repository,
    FileOperationQueue queue) : IFileOperationService
{
    public Task<FileOperationPreview> PreviewAsync(
        FileOperationPreviewRequest request,
        CancellationToken cancellationToken) =>
        planner.PreviewAsync(request, cancellationToken);

    public async Task<FileOperationStatus> SubmitAsync(
        FileOperationSubmission request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var plan = await planner.GetValidatedPlanAsync(request.PlanId, cancellationToken);
        if (plan.Kind is not (FileOperationKind.Copy or FileOperationKind.Move))
        {
            throw new InvalidOperationSelectionException();
        }

        ValidateResolutions(plan, request.Resolutions);
        var status = await repository.EnqueueAsync(
            plan,
            new(request.Resolutions, false),
            cancellationToken);
        queue.Signal();
        return status;
    }

    public Task<IReadOnlyList<FileOperationStatus>> ListAsync(
        CancellationToken cancellationToken) =>
        repository.ListAsync(cancellationToken);

    public Task<FileOperationStatus> GetAsync(
        Guid operationId,
        CancellationToken cancellationToken) =>
        repository.GetAsync(operationId, cancellationToken);

    public async Task<FileOperationStatus> CancelAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var status = await repository.RequestCancellationAsync(operationId, cancellationToken);
        queue.Signal();
        return status;
    }

    public Task AcknowledgeAsync(
        Guid operationId,
        CancellationToken cancellationToken) =>
        repository.AcknowledgeAsync(operationId, cancellationToken);

    private static void ValidateResolutions(
        FileOperationPlan plan,
        IReadOnlyList<FileOperationConflictResolution> resolutions)
    {
        var conflicts = plan.Conflicts.ToDictionary(conflict => conflict.ConflictId);
        if (resolutions is null ||
            resolutions.Count != conflicts.Count ||
            resolutions.Select(resolution => resolution.ConflictId).Distinct().Count() !=
                resolutions.Count ||
            resolutions.Any(resolution =>
                !conflicts.TryGetValue(resolution.ConflictId, out var conflict) ||
                !conflict.AllowedDecisions.Contains(resolution.Decision)))
        {
            throw new DestinationConflictException();
        }
    }
}

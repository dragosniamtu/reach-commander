using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using ReachCommander.Application.Archives;

namespace ReachCommander.Infrastructure.Archives.Extraction;

internal interface IArchiveOperationIdGenerator
{
    string CreateId();
}

internal sealed class ArchiveOperationIdGenerator : IArchiveOperationIdGenerator
{
    public string CreateId()
    {
        Span<byte> value = stackalloc byte[32];
        RandomNumberGenerator.Fill(value);
        return Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}

internal sealed class ArchiveExtractionService(
    ArchiveExtractionPlanner planner,
    ArchiveExtractionPlanStore plans,
    ArchiveExtractionOperationStore operations,
    ArchiveExtractionCoordinator coordinator,
    IArchiveOperationIdGenerator idGenerator,
    IOptions<ArchiveOptions> options) : IArchiveExtractionService
{
    private readonly object _gate = new();
    private readonly int _maximumConcurrent = options.Value.MaxConcurrentExtractions;
    private int _active;

    public ValueTask<ArchiveExtractionPreview> PreviewAsync(
        ArchiveExtractionPreviewRequest request,
        CancellationToken cancellationToken) =>
        planner.PreviewAsync(request, cancellationToken);

    public ValueTask<ArchiveExtractionOperation> ExecuteAsync(
        string planId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string operationId;
        ArchiveExtractionPlan plan;
        ArchiveExtractionOperation operation;
        lock (_gate)
        {
            var proposedId = idGenerator.CreateId();
            operationId = plans.BindOperation(planId, proposedId);
            if (!operationId.Equals(proposedId, StringComparison.Ordinal))
            {
                return ValueTask.FromResult(operations.GetRequired(operationId));
            }

            if (_active >= _maximumConcurrent)
            {
                plans.ReleaseBinding(planId, proposedId);
                throw new ArchiveCapacityReachedException();
            }

            plan = plans.GetRequiredPlan(planId);
            _active++;
            try
            {
                operation = operations.Create(operationId, plan);
            }
            catch
            {
                _active--;
                plans.ReleaseBinding(planId, proposedId);
                throw;
            }
        }

        _ = Task.Run(
            () => RunSupervisedAsync(plan, operationId),
            CancellationToken.None);
        return ValueTask.FromResult(operation);
    }

    public ValueTask<ArchiveExtractionOperation> GetAsync(
        string operationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(operations.GetRequired(operationId));
    }

    public ValueTask<ArchiveExtractionOperation> CancelAsync(
        string operationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(operations.RequestCancellation(operationId));
    }

    private async Task RunSupervisedAsync(
        ArchiveExtractionPlan plan,
        string operationId)
    {
        try
        {
            await coordinator.RunAsync(
                plan,
                operationId,
                operations.GetCancellationToken(operationId)).ConfigureAwait(false);
        }
        catch
        {
            operations.MarkFailed(operationId, new ArchiveWorkerFailedException());
        }
        finally
        {
            lock (_gate)
            {
                _active--;
            }
        }
    }
}

using Microsoft.Extensions.Logging;
using ReachCommander.Application.TextEncodings;

namespace ReachCommander.Infrastructure.TextEncodings;

internal sealed class TextEncodingService(
    TextEncodingPlanner planner,
    TextEncodingPlanStore planStore,
    TextEncodingOperationStore operationStore,
    ITextEncodingExecutor executor,
    ILogger<TextEncodingService> logger) : ITextEncodingService
{
    private readonly object _capacityGate = new();
    private Guid? _activeOperationId;

    public ValueTask<TextEncodingPreview> PreviewAsync(
        TextEncodingPreviewRequest request,
        CancellationToken cancellationToken) => planner.PreviewAsync(request, cancellationToken);

    public ValueTask<TextEncodingOperation> ExecuteAsync(
        Guid planId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StoredTextEncodingPlan plan;
        Guid operationId;
        TextEncodingOperation queued;
        lock (_capacityGate)
        {
            plan = planStore.Get(planId);
            if (plan.BoundOperationId is { } existingOperationId)
            {
                return ValueTask.FromResult(operationStore.GetRequired(existingOperationId));
            }

            if (_activeOperationId is not null)
            {
                throw TextEncodingException.CapacityReached();
            }

            operationId = planStore.BindOperation(planId, Guid.NewGuid());
            queued = operationStore.Create(operationId, plan.Entries);
            _activeOperationId = operationId;
        }

        _ = Task.Run(
            () => RunSupervisedAsync(plan, operationId),
            CancellationToken.None);
        return ValueTask.FromResult(queued);
    }

    public ValueTask<TextEncodingOperation> GetAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(operationStore.GetRequired(operationId));
    }

    public ValueTask<TextEncodingOperation> CancelAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(operationStore.RequestCancellation(operationId));
    }

    private async Task RunSupervisedAsync(
        StoredTextEncodingPlan plan,
        Guid operationId)
    {
        var operationCancellation = operationStore.GetCancellationToken(operationId);
        try
        {
            await executor.RunAsync(plan, operationId, operationCancellation);
            operationStore.MarkTerminal(operationId, TextEncodingOperationState.Completed);
        }
        catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested)
        {
            operationStore.MarkTerminal(operationId, TextEncodingOperationState.Cancelled);
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Supervised text encoding operation {OperationId} failed with {ExceptionType} ({HResult}).",
                operationId,
                exception.GetType().Name,
                exception.HResult);
            operationStore.MarkTerminal(
                operationId,
                TextEncodingOperationState.Failed,
                "text_encoding_operation_failed",
                "The encoding operation failed unexpectedly.");
        }
        finally
        {
            lock (_capacityGate)
            {
                if (_activeOperationId == operationId)
                {
                    _activeOperationId = null;
                }
            }
        }
    }
}

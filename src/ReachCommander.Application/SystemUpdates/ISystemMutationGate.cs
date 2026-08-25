namespace ReachCommander.Application.SystemUpdates;

public interface ISystemMutationGate
{
    IAsyncDisposable? TryEnter();

    Task<bool> BeginDrainAsync(TimeSpan timeout, CancellationToken cancellationToken);

    void CancelDrain();
}

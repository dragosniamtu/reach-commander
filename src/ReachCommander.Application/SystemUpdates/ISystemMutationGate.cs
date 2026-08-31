namespace ReachCommander.Application.SystemUpdates;

public interface ISystemMutationGate
{
    IAsyncDisposable? TryEnter();

    Task<ISystemMutationDrain?> BeginDrainAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

public interface ISystemMutationDrain : IAsyncDisposable;

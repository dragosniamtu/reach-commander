namespace ReachCommander.Application.SystemUpdates;

public interface ISystemUpdateService
{
    Task<SystemUpdateStatus> GetAsync(CancellationToken cancellationToken);

    Task<SystemUpdateStatus> CheckAsync(CancellationToken cancellationToken);

    Task<SystemUpdateStatus> ApplyAsync(CancellationToken cancellationToken);
}

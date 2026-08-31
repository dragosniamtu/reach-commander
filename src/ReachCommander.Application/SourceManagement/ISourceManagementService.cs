namespace ReachCommander.Application.SourceManagement;

public interface ISourceManagementService
{
    Task<SourceManagementCapability> GetStatusAsync(CancellationToken cancellationToken);

    Task<SourceManagementOperation> AddAsync(
        SourceAddRequest request,
        CancellationToken cancellationToken);

    Task<SourceManagementOperation> RemoveAsync(
        string sourceId,
        CancellationToken cancellationToken);

    Task<SourceManagementOperation> GetOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken);
}

public interface ISourceManagementGateway : ISourceManagementService;

public interface ISourceManagementOperationEligibility
{
    Task<bool> HasActiveOperationsAsync(CancellationToken cancellationToken);
}

public interface ISourceManagementRequestIdGenerator
{
    Guid NewId();
}

public interface ISourceManagementMonitorDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

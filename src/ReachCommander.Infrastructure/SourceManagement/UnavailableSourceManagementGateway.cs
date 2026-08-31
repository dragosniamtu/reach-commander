using ReachCommander.Application.SourceManagement;

namespace ReachCommander.Infrastructure.SourceManagement;

internal sealed class UnavailableSourceManagementGateway(bool unsupportedPlatform)
    : ISourceManagementGateway
{
    public Task<SourceManagementCapability> GetStatusAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(unsupportedPlatform
            ? new SourceManagementCapability(
                false,
                "unsupported_platform",
                "Source management is unavailable on this platform.")
            : new SourceManagementCapability(
                false,
                "unsupported_deployment",
                "Source management is unavailable on this installation."));
    }

    public Task<SourceManagementOperation> AddAsync(
        SourceAddRequest request,
        CancellationToken cancellationToken) =>
        throw new SourceManagementUnavailableException();

    public Task<SourceManagementOperation> RemoveAsync(
        string sourceId,
        CancellationToken cancellationToken) =>
        throw new SourceManagementUnavailableException();

    public Task<SourceManagementOperation> GetOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken) =>
        throw new SourceManagementUnavailableException();
}

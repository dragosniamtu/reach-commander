using ReachCommander.Application.SourceManagement;

namespace ReachCommander.Infrastructure.SourceManagement;

internal sealed class SourceManagementRequestIdGenerator : ISourceManagementRequestIdGenerator
{
    public Guid NewId() => Guid.NewGuid();
}

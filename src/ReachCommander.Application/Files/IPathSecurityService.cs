namespace ReachCommander.Application.Files;

public interface IPathSecurityService
{
    ValueTask<ResolvedSourcePath> ResolveAsync(
        string sourceId,
        string logicalPath,
        CancellationToken cancellationToken);

    ValueTask<ResolvedSourcePath> ResolveChildAsync(
        string sourceId,
        string parentLogicalPath,
        string childName,
        CancellationToken cancellationToken);
}

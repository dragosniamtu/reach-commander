namespace ReachCommander.Application.Files;

public interface IPathSecurityService
{
    ValueTask<ResolvedSourcePath> ResolveAsync(
        string sourceId,
        string logicalPath,
        CancellationToken cancellationToken);
}

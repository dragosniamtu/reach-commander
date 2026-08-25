using ReachCommander.Domain.Files;

namespace ReachCommander.Application.Directories;

public interface IDirectoryMutationService
{
    Task<FileEntry> CreateAsync(
        CreateDirectoryRequest request,
        CancellationToken cancellationToken);
}

using ReachCommander.Application.Directories;
using ReachCommander.Application.FileOperations;
using ReachCommander.Application.Files;
using ReachCommander.Application.Sources;
using ReachCommander.Domain.Files;
using ReachCommander.Infrastructure.BatchRenames;
using ReachCommander.Infrastructure.FileOperations.Planning;
using ReachCommander.Infrastructure.Mutations;

namespace ReachCommander.Infrastructure.Directories;

internal sealed class DirectoryMutationService(
    ISourceCatalog sourceCatalog,
    IPathSecurityService pathSecurity,
    IFileBrowser fileBrowser,
    IFileOperationInspector inspector,
    DirectoryMutationLock mutationLock,
    RenameNameValidator nameValidator) : IDirectoryMutationService
{
    public async Task<FileEntry> CreateAsync(
        CreateDirectoryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validation = nameValidator.Validate(request.Name);
        if (!validation.IsValid)
        {
            throw new InvalidDirectoryNameException();
        }

        var source = await sourceCatalog.GetRequiredAsync(
            request.SourceId,
            cancellationToken);
        if (source.IsReadOnly)
        {
            throw new OperationSourceReadOnlyException();
        }

        await using var lease = await mutationLock.AcquireAsync(
            source.Id,
            request.ParentLogicalPath,
            cancellationToken);
        var parent = await inspector.GetRequiredAsync(
            source.Id,
            request.ParentLogicalPath,
            cancellationToken);
        if (parent.IsSymbolicLink)
        {
            throw new UnsafeSymbolicLinkException();
        }

        if (parent.Type != FileEntryType.Directory)
        {
            throw new InvalidDirectoryNameException();
        }

        var destination = await pathSecurity.ResolveChildAsync(
            source.Id,
            request.ParentLogicalPath,
            request.Name,
            cancellationToken);
        if (await inspector.TryGetAsync(source.Id, destination.LogicalPath, cancellationToken) is not null)
        {
            throw new DestinationConflictException();
        }

        Directory.CreateDirectory(destination.PhysicalPath);
        return await fileBrowser.GetInfoAsync(
            source.Id,
            destination.LogicalPath,
            cancellationToken);
    }
}

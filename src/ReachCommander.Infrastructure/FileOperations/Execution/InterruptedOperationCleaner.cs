using ReachCommander.Application.FileOperations;
using ReachCommander.Application.Files;
using ReachCommander.Infrastructure.FileOperations.Persistence;

namespace ReachCommander.Infrastructure.FileOperations.Execution;

internal sealed class InterruptedOperationCleaner(
    IPathSecurityService pathSecurity,
    IFileOperationFileSystem fileSystem)
{
    private const string CleanupWarning =
        "An interrupted operation entry could not be cleaned safely.";

    internal async Task<IReadOnlyList<string>> CleanupAsync(
        PersistedFileOperationDocument operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var warnings = new List<string>();
        if (operation.Journal is null)
        {
            return warnings;
        }

        if (operation.Journal.OperationId != operation.OperationId)
        {
            return [CleanupWarning];
        }

        foreach (var entry in operation.Journal.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await CleanupEntryAsync(operation.OperationId, entry, cancellationToken);
            }
            catch (Exception exception) when (exception is
                IOException or
                UnauthorizedAccessException or
                ArgumentException or
                FileOperationException)
            {
                warnings.Add(CleanupWarning);
            }
        }

        return warnings;
    }

    private async Task CleanupEntryAsync(
        Guid operationId,
        FileOperationJournalEntry entry,
        CancellationToken cancellationToken)
    {
        ValidateOwnedName(operationId, entry.OwnedName);
        var parent = await pathSecurity.ResolveAsync(
            entry.SourceId,
            entry.ParentLogicalPath,
            cancellationToken);
        var ownedPath = Path.Combine(parent.PhysicalPath, entry.OwnedName);
        if (!fileSystem.Exists(ownedPath))
        {
            return;
        }

        var attributes = fileSystem.GetAttributes(ownedPath);
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("Recovery never follows links.");
        }

        if (!entry.IsQuarantine)
        {
            DeleteExact(ownedPath, attributes);
            return;
        }

        if (string.IsNullOrWhiteSpace(entry.PublicDestinationLogicalPath) ||
            !Parent(entry.PublicDestinationLogicalPath).Equals(
                entry.ParentLogicalPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The recovery destination is invalid.");
        }

        var destination = await pathSecurity.ResolveChildAsync(
            entry.SourceId,
            entry.ParentLogicalPath,
            Name(entry.PublicDestinationLogicalPath),
            cancellationToken);
        if (fileSystem.Exists(destination.PhysicalPath))
        {
            DeleteExact(ownedPath, attributes);
            return;
        }

        if (fileSystem.TryMove(ownedPath, destination.PhysicalPath) != MoveAttempt.Moved)
        {
            throw new IOException("Recovery quarantine unexpectedly crossed filesystems.");
        }
    }

    private static void ValidateOwnedName(Guid operationId, string ownedName)
    {
        var expectedPrefix =
            $"{ReservedFileOperationPathPolicy.OperationPrefix}{operationId:N}-";
        if (string.IsNullOrWhiteSpace(ownedName) ||
            ownedName != Path.GetFileName(ownedName) ||
            ownedName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0 ||
            !ownedName.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The recovery entry is outside its allowlist.");
        }
    }

    private void DeleteExact(string physicalPath, FileAttributes attributes)
    {
        if (attributes.HasFlag(FileAttributes.Directory))
        {
            fileSystem.DeleteDirectory(physicalPath, recursive: false);
        }
        else
        {
            fileSystem.DeleteFile(physicalPath);
        }
    }

    private static string Parent(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator <= 0 ? "/" : path[..separator];
    }

    private static string Name(string path) => path[(path.LastIndexOf('/') + 1)..];
}

using System.Text.Json;
using ReachCommander.Application.FileOperations;
using ReachCommander.Application.Files;
using ReachCommander.Application.Sources;
using ReachCommander.Domain.Files;
using ReachCommander.Domain.Sources;
using ReachCommander.Infrastructure.FileOperations;
using ReachCommander.Infrastructure.FileOperations.Persistence;
using ReachCommander.Infrastructure.FileOperations.Planning;

namespace ReachCommander.Infrastructure.Trash;

internal sealed class TrashManifestStore(
    ISourceCatalog sourceCatalog,
    IPathSecurityService pathSecurity)
{
    private const string OwnershipValue = "ReachCommander managed Trash";
    private const string UnavailableReason =
        "The source-local Trash cannot be safely owned by ReachCommander.";
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal async Task<TrashCapability> GetCapabilityAsync(
        string sourceId,
        CancellationToken cancellationToken)
    {
        var source = await sourceCatalog.GetRequiredAsync(sourceId, cancellationToken);
        if (source.IsReadOnly)
        {
            return new(false, "The source is read-only.");
        }

        var root = await pathSecurity.ResolveAsync(source.Id, "/", cancellationToken);
        var trashRoot = Path.Combine(root.PhysicalPath, TrashLayout.Root);
        if (!Directory.Exists(trashRoot) && !File.Exists(trashRoot))
        {
            return new(true, null);
        }

        return await IsOwnedRootAsync(trashRoot, cancellationToken) &&
            HasOnlySafeLayoutChildren(trashRoot)
            ? new(true, null)
            : new(false, UnavailableReason);
    }

    internal async Task<TrashStoragePaths> GetOrCreatePathsAsync(
        string sourceId,
        Guid trashId,
        CancellationToken cancellationToken)
    {
        if (trashId == Guid.Empty)
        {
            throw new TrashManifestInvalidException();
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var capability = await GetCapabilityAsync(sourceId, cancellationToken);
            if (!capability.IsAvailable)
            {
                throw new TrashUnavailableException();
            }

            var root = await pathSecurity.ResolveAsync(sourceId, "/", cancellationToken);
            var trashRoot = Path.Combine(root.PhysicalPath, TrashLayout.Root);
            if (!Directory.Exists(trashRoot))
            {
                Directory.CreateDirectory(trashRoot);
                await AtomicJsonFile.WriteAsync(
                    Path.Combine(trashRoot, TrashLayout.OwnershipMarker),
                    new TrashOwnershipMarker(1, OwnershipValue),
                    cancellationToken);
            }

            EnsureOwnedDirectory(Path.Combine(trashRoot, TrashLayout.Manifests));
            EnsureOwnedDirectory(Path.Combine(trashRoot, TrashLayout.Items));
            EnsureOwnedDirectory(Path.Combine(trashRoot, TrashLayout.Staging));
            return Paths(trashRoot, trashId);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task WriteManifestAsync(
        TrashManifest manifest,
        CancellationToken cancellationToken)
    {
        ValidateManifestShape(manifest);
        var paths = await GetOrCreatePathsAsync(
            manifest.SourceId,
            manifest.TrashId,
            cancellationToken);
        await AtomicJsonFile.WriteAsync(
            paths.ManifestPhysicalPath,
            manifest,
            cancellationToken);
    }

    internal async Task<IReadOnlyList<ValidTrashRecord>> LoadValidAsync(
        string? sourceId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SourceDefinition> sources = sourceId is null
            ? await sourceCatalog.GetDefinitionsAsync(cancellationToken)
            : [await sourceCatalog.GetRequiredAsync(sourceId, cancellationToken)];
        var records = new List<ValidTrashRecord>();
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = await pathSecurity.ResolveAsync(source.Id, "/", cancellationToken);
            var trashRoot = Path.Combine(root.PhysicalPath, TrashLayout.Root);
            if (!Directory.Exists(trashRoot) ||
                !await IsOwnedRootAsync(trashRoot, cancellationToken))
            {
                continue;
            }

            var manifests = Path.Combine(trashRoot, TrashLayout.Manifests);
            if (!Directory.Exists(manifests))
            {
                continue;
            }

            foreach (var manifestPath in Directory.EnumerateFiles(manifests, "*.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var attributes = File.GetAttributes(manifestPath);
                    if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        continue;
                    }

                    var manifest = await AtomicJsonFile.ReadAsync<TrashManifest>(
                        manifestPath,
                        cancellationToken);
                    ValidateManifestShape(manifest);
                    if (!manifest.SourceId.Equals(source.Id, StringComparison.Ordinal) ||
                        !Path.GetFileNameWithoutExtension(manifestPath).Equals(
                            manifest.TrashId.ToString("N"),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var paths = Paths(trashRoot, manifest.TrashId);
                    if (!Path.GetFullPath(paths.ManifestPhysicalPath).Equals(
                            Path.GetFullPath(manifestPath),
                            PathComparison) ||
                        !ItemAgrees(manifest, paths.ItemPhysicalPath))
                    {
                        continue;
                    }

                    records.Add(new(manifest, paths));
                }
                catch (Exception exception) when (exception is
                    IOException or
                    UnauthorizedAccessException or
                    JsonException or
                    InvalidDataException or
                    FileOperationException)
                {
                    // Invalid records are isolated and left for operator inspection.
                }
            }
        }

        return records
            .OrderByDescending(record => record.Manifest.DeletedAt)
            .ThenBy(record => record.Manifest.TrashId)
            .ToArray();
    }

    internal async Task<ValidTrashRecord> GetRequiredAsync(
        Guid trashId,
        CancellationToken cancellationToken)
    {
        var record = (await LoadValidAsync(null, cancellationToken))
            .SingleOrDefault(candidate => candidate.Manifest.TrashId == trashId);
        return record ?? throw new TrashManifestInvalidException();
    }

    internal void RemoveMetadata(ValidTrashRecord record)
    {
        File.Delete(record.Paths.ManifestPhysicalPath);
        if (Directory.Exists(record.Paths.ItemContainerPhysicalPath) &&
            !Directory.EnumerateFileSystemEntries(record.Paths.ItemContainerPhysicalPath).Any())
        {
            Directory.Delete(record.Paths.ItemContainerPhysicalPath, recursive: false);
        }
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static TrashStoragePaths Paths(string trashRoot, Guid trashId)
    {
        var id = trashId.ToString("N");
        var itemContainer = Path.Combine(trashRoot, TrashLayout.Items, id);
        var stagingContainer = Path.Combine(trashRoot, TrashLayout.Staging, id);
        return new(
            trashRoot,
            Path.Combine(trashRoot, TrashLayout.Manifests, $"{id}.json"),
            itemContainer,
            Path.Combine(itemContainer, TrashLayout.ItemName),
            stagingContainer,
            Path.Combine(stagingContainer, TrashLayout.ItemName));
    }

    private static async Task<bool> IsOwnedRootAsync(
        string trashRoot,
        CancellationToken cancellationToken)
    {
        try
        {
            var attributes = File.GetAttributes(trashRoot);
            if (!attributes.HasFlag(FileAttributes.Directory) ||
                attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }

            var markerPath = Path.Combine(trashRoot, TrashLayout.OwnershipMarker);
            if (!File.Exists(markerPath) ||
                File.GetAttributes(markerPath).HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }

            var marker = await AtomicJsonFile.ReadAsync<TrashOwnershipMarker>(
                markerPath,
                cancellationToken);
            return marker == new TrashOwnershipMarker(1, OwnershipValue);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static void EnsureOwnedDirectory(string path)
    {
        if (File.Exists(path))
        {
            throw new TrashUnavailableException();
        }

        Directory.CreateDirectory(path);
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new TrashUnavailableException();
        }
    }

    private static bool ItemAgrees(TrashManifest manifest, string itemPath)
    {
        FileSystemInfo? item = manifest.Type switch
        {
            FileEntryType.File when File.Exists(itemPath) => new FileInfo(itemPath),
            FileEntryType.Directory when Directory.Exists(itemPath) => new DirectoryInfo(itemPath),
            _ => null,
        };
        if (item is null)
        {
            return false;
        }

        item.Refresh();
        if (item.LinkTarget is not null || item.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return false;
        }

        var fingerprint = new FileOperationEntryFingerprint(
            manifest.Type,
            item is FileInfo file ? file.Length : null,
            new DateTimeOffset(item.LastWriteTimeUtc),
            item.Attributes,
            false);
        return fingerprint == manifest.Fingerprint;
    }

    private static bool HasOnlySafeLayoutChildren(string trashRoot)
    {
        try
        {
            foreach (var name in new[] { TrashLayout.Manifests, TrashLayout.Items, TrashLayout.Staging })
            {
                var path = Path.Combine(trashRoot, name);
                if (!Directory.Exists(path) && !File.Exists(path))
                {
                    continue;
                }

                var attributes = File.GetAttributes(path);
                if (!attributes.HasFlag(FileAttributes.Directory) ||
                    attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void ValidateManifestShape(TrashManifest manifest)
    {
        if (manifest.SchemaVersion != TrashManifest.CurrentSchemaVersion ||
            manifest.TrashId == Guid.Empty ||
            string.IsNullOrWhiteSpace(manifest.SourceId) ||
            string.IsNullOrWhiteSpace(manifest.OriginalLogicalPath) ||
            manifest.OriginalLogicalPath == "/" ||
            !manifest.OriginalLogicalPath.StartsWith('/') ||
            manifest.OriginalLogicalPath.Contains('\\') ||
            manifest.OriginalLogicalPath.Contains("//", StringComparison.Ordinal) ||
            manifest.OriginalLogicalPath.Split('/').Any(segment => segment is "." or "..") ||
            ReservedFileOperationPathPolicy.ContainsReservedSegment(manifest.OriginalLogicalPath) ||
            manifest.OriginalName != Name(manifest.OriginalLogicalPath) ||
            manifest.StoredRelativeItemPath != $"items/{manifest.TrashId:N}/item" ||
            manifest.Fingerprint.IsSymbolicLink ||
            manifest.Fingerprint.Type != manifest.Type ||
            manifest.Fingerprint.Length != manifest.Size ||
            manifest.Type is not (FileEntryType.File or FileEntryType.Directory))
        {
            throw new TrashManifestInvalidException();
        }
    }

    private static string Name(string path) => path[(path.LastIndexOf('/') + 1)..];

    private sealed record TrashOwnershipMarker(int SchemaVersion, string Owner);
}

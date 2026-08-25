using ReachCommander.Application.FileOperations;
using ReachCommander.Application.Sources;
using ReachCommander.Domain.Files;
using ReachCommander.Infrastructure.Mutations;

namespace ReachCommander.Infrastructure.FileOperations.Planning;

internal sealed class FileOperationPlanner(
    ISourceCatalog sourceCatalog,
    IFileOperationInspector inspector,
    IFileOperationPlanStore planStore,
    TimeProvider clock)
{
    private static readonly TimeSpan PlanLifetime = TimeSpan.FromMinutes(10);
    private static readonly IReadOnlyList<FileOperationConflictDecision> ConflictDecisions =
        Array.AsReadOnly(
        [
            FileOperationConflictDecision.Overwrite,
            FileOperationConflictDecision.Skip,
            FileOperationConflictDecision.CreateUniqueName,
        ]);

    internal async Task<FileOperationPreview> PreviewAsync(
        FileOperationPreviewRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Kind is not (FileOperationKind.Copy or FileOperationKind.Move))
        {
            throw new InvalidOperationSelectionException();
        }

        var source = await sourceCatalog.GetRequiredAsync(request.SourceId, cancellationToken);
        var destination = await sourceCatalog.GetRequiredAsync(
            request.DestinationSourceId,
            cancellationToken);
        if (request.Kind == FileOperationKind.Move && source.IsReadOnly)
        {
            throw new OperationSourceReadOnlyException();
        }

        if (destination.IsReadOnly)
        {
            throw new OperationSourceReadOnlyException();
        }

        var sourcePaths = NormalizeSelection(request.LogicalPaths);
        var destinationDirectory = NormalizeLogicalPath(request.DestinationLogicalDirectory);
        var destinationSnapshot = await inspector.GetRequiredAsync(
            destination.Id,
            destinationDirectory,
            cancellationToken);
        RequireSupported(destinationSnapshot, requireDirectory: true);

        var planned = new List<PlannedFileOperationEntry>();
        var conflicts = new List<FileOperationConflict>();
        var lockTargets = new List<DirectoryMutationTarget>
        {
            new(destination.Id, destinationDirectory),
        };
        long totalBytes = 0;
        var allBytesKnown = true;

        foreach (var sourcePath in sourcePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceEntry = await inspector.GetRequiredAsync(
                source.Id,
                sourcePath,
                cancellationToken);
            RequireSupported(sourceEntry, requireDirectory: false);
            if (source.Id.Equals(destination.Id, StringComparison.OrdinalIgnoreCase) &&
                sourceEntry.Type == FileEntryType.Directory &&
                IsSameOrDescendant(sourcePath, destinationDirectory))
            {
                throw new InvalidOperationSelectionException();
            }

            lockTargets.Add(new(source.Id, Parent(sourcePath)));
            var destinationPath = Join(destinationDirectory, sourceEntry.Name);
            await PlanEntryAsync(
                sourceEntry,
                destination.Id,
                destinationPath,
                sourcePath,
                isTopLevel: true,
                planned,
                conflicts,
                bytes =>
                {
                    if (bytes is null) allBytesKnown = false;
                    else totalBytes = checked(totalBytes + bytes.Value);
                },
                cancellationToken);
        }

        if (allBytesKnown &&
            (request.Kind == FileOperationKind.Copy ||
             !source.Id.Equals(destination.Id, StringComparison.OrdinalIgnoreCase)))
        {
            var availableBytes = await inspector.GetAvailableBytesAsync(
                destination.Id,
                destinationDirectory,
                cancellationToken);
            if (availableBytes is not null && totalBytes > availableBytes.Value)
            {
                throw new InsufficientStorageException();
            }
        }

        var now = clock.GetUtcNow();
        var plan = new FileOperationPlan(
            Guid.NewGuid(),
            now,
            now.Add(PlanLifetime),
            request.Kind,
            source.Id,
            sourcePaths,
            destination.Id,
            destinationDirectory,
            planned.AsReadOnly(),
            [],
            null,
            conflicts.AsReadOnly(),
            lockTargets.Distinct().ToArray(),
            allBytesKnown ? totalBytes : null);
        await planStore.SaveAsync(plan, cancellationToken);
        return new FileOperationPreview(
            plan.PlanId,
            plan.ExpiresAt,
            plan.Kind,
            source.Id,
            sourcePaths,
            destination.Id,
            destinationDirectory,
            planned.Count,
            plan.TotalBytes,
            conflicts.AsReadOnly(),
            []);
    }

    internal async Task<FileOperationPlan> GetValidatedPlanAsync(
        Guid planId,
        CancellationToken cancellationToken)
    {
        var plan = await planStore.GetAsync(planId, cancellationToken)
            ?? throw new OperationPlanNotFoundException();
        if (plan.ExpiresAt <= clock.GetUtcNow())
        {
            await planStore.DeleteAsync(planId, cancellationToken);
            throw new OperationPlanExpiredException();
        }

        return plan;
    }

    private async Task PlanEntryAsync(
        FileOperationEntrySnapshot source,
        string destinationSourceId,
        string destinationPath,
        string topLevelSourcePath,
        bool isTopLevel,
        ICollection<PlannedFileOperationEntry> planned,
        ICollection<FileOperationConflict> conflicts,
        Action<long?> addBytes,
        CancellationToken cancellationToken)
    {
        RequireSupported(source, requireDirectory: false);
        var existing = await inspector.TryGetAsync(
            destinationSourceId,
            destinationPath,
            cancellationToken);
        Guid? conflictId = null;
        if (existing is not null)
        {
            RequireSupported(existing, requireDirectory: false);
            conflictId = Guid.NewGuid();
            conflicts.Add(new FileOperationConflict(
                conflictId.Value,
                source.LogicalPath,
                destinationPath,
                source.Type,
                existing.Type,
                ConflictDecisions));
        }

        planned.Add(new PlannedFileOperationEntry(
            source.LogicalPath,
            destinationPath,
            topLevelSourcePath,
            source.Fingerprint,
            conflictId,
            isTopLevel));
        if (source.Type == FileEntryType.File)
        {
            addBytes(source.Length);
            return;
        }

        var children = await inspector.ListChildrenAsync(
            source.SourceId,
            source.LogicalPath,
            cancellationToken);
        foreach (var child in children)
        {
            await PlanEntryAsync(
                child,
                destinationSourceId,
                Join(destinationPath, child.Name),
                topLevelSourcePath,
                isTopLevel: false,
                planned,
                conflicts,
                addBytes,
                cancellationToken);
        }
    }

    private static void RequireSupported(
        FileOperationEntrySnapshot entry,
        bool requireDirectory)
    {
        if (entry.IsSymbolicLink)
        {
            throw new UnsafeSymbolicLinkException();
        }

        if (entry.Type is not (FileEntryType.File or FileEntryType.Directory) ||
            (requireDirectory && entry.Type != FileEntryType.Directory))
        {
            throw new InvalidOperationSelectionException();
        }
    }

    private static IReadOnlyList<string> NormalizeSelection(IReadOnlyList<string>? paths)
    {
        if (paths is null or { Count: 0 } || paths.Count > 5_000)
        {
            throw new InvalidOperationSelectionException();
        }

        var normalized = paths.Select(NormalizeLogicalPath).ToArray();
        if (normalized.Any(path => path == "/"))
        {
            throw new InvalidOperationSelectionException();
        }

        for (var index = 0; index < normalized.Length; index++)
        {
            for (var other = 0; other < normalized.Length; other++)
            {
                if (index != other && IsSameOrDescendant(normalized[index], normalized[other]))
                {
                    throw new InvalidOperationSelectionException();
                }
            }
        }

        return Array.AsReadOnly(normalized);
    }

    private static string NormalizeLogicalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !path.StartsWith('/') ||
            path.StartsWith("//", StringComparison.Ordinal) ||
            (path.Length > 1 && path.EndsWith('/')) ||
            path.Contains("//", StringComparison.Ordinal) ||
            path.Contains('\\') ||
            path.Contains('\0'))
        {
            throw new InvalidOperationSelectionException();
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment =>
                segment is "." or ".." ||
                ReservedFileOperationPathPolicy.IsReservedName(segment)))
        {
            throw new InvalidOperationSelectionException();
        }

        return segments.Length == 0 ? "/" : $"/{string.Join('/', segments)}";
    }

    private static bool IsSameOrDescendant(string ancestor, string path) =>
        path.Equals(ancestor, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith($"{ancestor}/", StringComparison.OrdinalIgnoreCase);

    private static string Parent(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator <= 0 ? "/" : path[..separator];
    }

    private static string Join(string parent, string name) =>
        parent == "/" ? $"/{name}" : $"{parent}/{name}";
}

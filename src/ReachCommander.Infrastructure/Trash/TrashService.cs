using ReachCommander.Application.FileOperations;
using ReachCommander.Application.Sources;
using ReachCommander.Application.Trash;
using ReachCommander.Domain.Files;
using ReachCommander.Infrastructure.FileOperations.Persistence;
using ReachCommander.Infrastructure.FileOperations.Planning;
using ReachCommander.Infrastructure.Mutations;

namespace ReachCommander.Infrastructure.Trash;

internal sealed class TrashService(
    ISourceCatalog sourceCatalog,
    IFileOperationInspector inspector,
    TrashManifestStore manifestStore,
    IFileOperationPlanStore planStore,
    FileOperationRepository repository,
    TimeProvider clock) : ITrashService
{
    private static readonly TimeSpan PlanLifetime = TimeSpan.FromMinutes(10);
    private static readonly IReadOnlyList<FileOperationConflictDecision> ConflictDecisions =
        Array.AsReadOnly(
        [
            FileOperationConflictDecision.Overwrite,
            FileOperationConflictDecision.Skip,
            FileOperationConflictDecision.CreateUniqueName,
        ]);

    public async Task<DeletePreview> PreviewDeleteAsync(
        DeletePreviewRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var source = await sourceCatalog.GetRequiredAsync(request.SourceId, cancellationToken);
        if (source.IsReadOnly)
        {
            throw new OperationSourceReadOnlyException();
        }

        var selected = await InspectSelectionAsync(
            source.Id,
            request.LogicalPaths,
            cancellationToken);
        var capability = request.Mode == DeleteMode.Trash
            ? await manifestStore.GetCapabilityAsync(source.Id, cancellationToken)
            : new TrashCapability(true, null);
        var now = clock.GetUtcNow();
        var planId = Guid.NewGuid();
        var topEntries = selected
            .Where(entry => entry.IsTopLevel)
            .ToArray();
        var entries = request.Mode == DeleteMode.Trash ? topEntries : selected;
        var trashIds = request.Mode == DeleteMode.Trash
            ? topEntries.Select(_ => Guid.NewGuid()).ToArray()
            : [];
        var totalBytes = SumBytes(selected);
        var plan = new FileOperationPlan(
            planId,
            now,
            now.Add(PlanLifetime),
            request.Mode == DeleteMode.Trash
                ? FileOperationKind.Trash
                : FileOperationKind.PermanentDelete,
            source.Id,
            topEntries.Select(entry => entry.SourceLogicalPath).ToArray(),
            null,
            null,
            entries,
            trashIds,
            null,
            [],
            [new DirectoryMutationTarget(source.Id, "/")],
            totalBytes);
        await planStore.SaveAsync(plan, cancellationToken);
        return new(
            planId,
            plan.ExpiresAt,
            request.Mode,
            capability.IsAvailable,
            capability.UnavailableReason,
            entries.Count,
            totalBytes);
    }

    public async Task<FileOperationStatus> SubmitDeleteAsync(
        DeleteSubmission request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var plan = await GetValidPlanAsync(request.PlanId, cancellationToken);
        if (plan.Kind is not (FileOperationKind.Trash or FileOperationKind.PermanentDelete) ||
            (plan.Kind == FileOperationKind.PermanentDelete && plan.TrashIds.Count != 0))
        {
            throw new InvalidOperationSelectionException();
        }

        if (plan.Kind == FileOperationKind.Trash)
        {
            var capability = await manifestStore.GetCapabilityAsync(plan.SourceId!, cancellationToken);
            if (!capability.IsAvailable)
            {
                throw new TrashUnavailableException();
            }
        }
        else if (!request.PermanentDeleteConfirmed)
        {
            throw new PermanentDeleteConfirmationRequiredException();
        }

        return await repository.EnqueueAsync(
            plan,
            new([], request.PermanentDeleteConfirmed),
            cancellationToken);
    }

    public async Task<IReadOnlyList<TrashEntry>> ListAsync(
        string? sourceId,
        CancellationToken cancellationToken) =>
        (await manifestStore.LoadValidAsync(sourceId, cancellationToken))
        .Select(ToEntry)
        .ToArray();

    public async Task<RestorePreview> PreviewRestoreAsync(
        RestorePreviewRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ids = NormalizeTrashIds(request.TrashIds);
        var records = new List<ValidTrashRecord>(ids.Count);
        foreach (var id in ids)
        {
            records.Add(await manifestStore.GetRequiredAsync(id, cancellationToken));
        }

        var conflicts = new List<FileOperationConflict>();
        var missingParents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var planned = new List<PlannedFileOperationEntry>(records.Count);
        foreach (var record in records)
        {
            var manifest = record.Manifest;
            var destination = await inspector.TryGetAsync(
                manifest.SourceId,
                manifest.OriginalLogicalPath,
                cancellationToken);
            Guid? conflictId = null;
            if (destination is not null)
            {
                conflictId = Guid.NewGuid();
                conflicts.Add(new(
                    conflictId.Value,
                    manifest.OriginalLogicalPath,
                    manifest.OriginalLogicalPath,
                    manifest.Type,
                    destination.Type,
                    ConflictDecisions));
            }

            await CollectMissingParentsAsync(
                manifest.SourceId,
                Parent(manifest.OriginalLogicalPath),
                missingParents,
                cancellationToken);
            planned.Add(new(
                manifest.OriginalLogicalPath,
                manifest.OriginalLogicalPath,
                manifest.OriginalLogicalPath,
                manifest.Fingerprint,
                destination?.Fingerprint,
                conflictId,
                true));
        }

        var now = clock.GetUtcNow();
        var plan = new FileOperationPlan(
            Guid.NewGuid(),
            now,
            now.Add(PlanLifetime),
            FileOperationKind.Restore,
            null,
            records.Select(record => record.Manifest.OriginalLogicalPath).ToArray(),
            null,
            null,
            planned,
            ids,
            null,
            conflicts,
            records.Select(record => new DirectoryMutationTarget(record.Manifest.SourceId, "/"))
                .Distinct()
                .ToArray(),
            SumManifestBytes(records));
        await planStore.SaveAsync(plan, cancellationToken);
        return new(
            plan.PlanId,
            plan.ExpiresAt,
            records.Select(record => ToEntry(record)).ToArray(),
            conflicts,
            missingParents.OrderBy(path => path, StringComparer.Ordinal).ToArray());
    }

    public async Task<FileOperationStatus> SubmitRestoreAsync(
        RestoreSubmission request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var plan = await GetValidPlanAsync(request.PlanId, cancellationToken);
        if (plan.Kind != FileOperationKind.Restore)
        {
            throw new InvalidOperationSelectionException();
        }

        ValidateResolutions(plan, request.Resolutions);
        return await repository.EnqueueAsync(
            plan,
            new(request.Resolutions, false),
            cancellationToken);
    }

    public async Task<FileOperationStatus> PermanentlyDeleteAsync(
        TrashPermanentDeleteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.PermanentDeleteConfirmed)
        {
            throw new PermanentDeleteConfirmationRequiredException();
        }

        var records = await LoadRecordsAsync(request.TrashIds, cancellationToken);
        var plan = await CreateTrashRecordDeletionPlanAsync(
            FileOperationKind.PermanentDelete,
            records,
            null,
            cancellationToken);
        return await repository.EnqueueAsync(plan, new([], true), cancellationToken);
    }

    public async Task<FileOperationStatus> EmptyAsync(
        EmptyTrashRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.PermanentDeleteConfirmed)
        {
            throw new PermanentDeleteConfirmationRequiredException();
        }

        var records = await manifestStore.LoadValidAsync(request.SourceId, cancellationToken);
        var plan = await CreateTrashRecordDeletionPlanAsync(
            FileOperationKind.EmptyTrash,
            records,
            request.SourceId,
            cancellationToken);
        return await repository.EnqueueAsync(plan, new([], true), cancellationToken);
    }

    private async Task<IReadOnlyList<PlannedFileOperationEntry>> InspectSelectionAsync(
        string sourceId,
        IReadOnlyList<string> logicalPaths,
        CancellationToken cancellationToken)
    {
        if (logicalPaths is null || logicalPaths.Count == 0)
        {
            throw new InvalidOperationSelectionException();
        }

        var selected = new List<FileOperationEntrySnapshot>();
        foreach (var requested in logicalPaths)
        {
            var snapshot = await inspector.GetRequiredAsync(sourceId, requested, cancellationToken);
            RequireSafe(snapshot);
            if (snapshot.LogicalPath == "/")
            {
                throw new InvalidOperationSelectionException();
            }

            selected.Add(snapshot);
        }

        if (selected.Select(item => item.LogicalPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
                selected.Count ||
            selected.Any(item => selected.Any(other =>
                !ReferenceEquals(item, other) &&
                IsDescendant(other.LogicalPath, item.LogicalPath))))
        {
            throw new InvalidOperationSelectionException();
        }

        var entries = new List<PlannedFileOperationEntry>();
        foreach (var top in selected)
        {
            await WalkAsync(top, top.LogicalPath, true, entries, cancellationToken);
        }

        return entries;
    }

    private async Task WalkAsync(
        FileOperationEntrySnapshot snapshot,
        string topLevel,
        bool isTopLevel,
        List<PlannedFileOperationEntry> entries,
        CancellationToken cancellationToken)
    {
        RequireSafe(snapshot);
        entries.Add(new(
            snapshot.LogicalPath,
            snapshot.LogicalPath,
            topLevel,
            snapshot.Fingerprint,
            null,
            null,
            isTopLevel));
        if (snapshot.Type != FileEntryType.Directory)
        {
            return;
        }

        foreach (var child in await inspector.ListChildrenAsync(
                     snapshot.SourceId,
                     snapshot.LogicalPath,
                     cancellationToken))
        {
            await WalkAsync(child, topLevel, false, entries, cancellationToken);
        }
    }

    private static void RequireSafe(FileOperationEntrySnapshot snapshot)
    {
        if (snapshot.IsSymbolicLink)
        {
            throw new UnsafeSymbolicLinkException();
        }

        if (snapshot.Type is not (FileEntryType.File or FileEntryType.Directory))
        {
            throw new InvalidOperationSelectionException();
        }
    }

    private async Task CollectMissingParentsAsync(
        string sourceId,
        string logicalParent,
        ISet<string> missing,
        CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        var current = logicalParent;
        while (current != "/")
        {
            var snapshot = await inspector.TryGetAsync(sourceId, current, cancellationToken);
            if (snapshot is not null)
            {
                RequireSafe(snapshot);
                if (snapshot.Type != FileEntryType.Directory)
                {
                    throw new TrashRestoreConflictException();
                }

                break;
            }

            pending.Push(current);
            current = Parent(current);
        }

        while (pending.Count > 0)
        {
            missing.Add(pending.Pop());
        }
    }

    private async Task<IReadOnlyList<ValidTrashRecord>> LoadRecordsAsync(
        IReadOnlyList<Guid> trashIds,
        CancellationToken cancellationToken)
    {
        var ids = NormalizeTrashIds(trashIds);
        var records = new List<ValidTrashRecord>(ids.Count);
        foreach (var id in ids)
        {
            records.Add(await manifestStore.GetRequiredAsync(id, cancellationToken));
        }

        return records;
    }

    private async Task<FileOperationPlan> CreateTrashRecordDeletionPlanAsync(
        FileOperationKind kind,
        IReadOnlyList<ValidTrashRecord> records,
        string? sourceScope,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var plan = new FileOperationPlan(
            Guid.NewGuid(),
            now,
            now.Add(PlanLifetime),
            kind,
            null,
            [],
            null,
            null,
            [],
            records.Select(record => record.Manifest.TrashId).ToArray(),
            sourceScope,
            [],
            records.Select(record => new DirectoryMutationTarget(record.Manifest.SourceId, "/"))
                .Distinct()
                .DefaultIfEmpty(new DirectoryMutationTarget("trash", "/"))
                .ToArray(),
            SumManifestBytes(records));
        await planStore.SaveAsync(plan, cancellationToken);
        return plan;
    }

    private async Task<FileOperationPlan> GetValidPlanAsync(
        Guid planId,
        CancellationToken cancellationToken)
    {
        var plan = await planStore.GetAsync(planId, cancellationToken)
            ?? throw new OperationPlanNotFoundException();
        if (plan.ExpiresAt <= clock.GetUtcNow())
        {
            throw new OperationPlanExpiredException();
        }

        return plan;
    }

    private static void ValidateResolutions(
        FileOperationPlan plan,
        IReadOnlyList<FileOperationConflictResolution> resolutions)
    {
        var conflicts = plan.Conflicts.ToDictionary(conflict => conflict.ConflictId);
        if (resolutions.Count != conflicts.Count ||
            resolutions.Select(resolution => resolution.ConflictId).Distinct().Count() !=
                resolutions.Count ||
            resolutions.Any(resolution =>
                !conflicts.TryGetValue(resolution.ConflictId, out var conflict) ||
                !conflict.AllowedDecisions.Contains(resolution.Decision)))
        {
            throw new DestinationConflictException();
        }
    }

    private static IReadOnlyList<Guid> NormalizeTrashIds(IReadOnlyList<Guid> ids)
    {
        if (ids is null || ids.Count == 0 || ids.Any(id => id == Guid.Empty) ||
            ids.Distinct().Count() != ids.Count)
        {
            throw new InvalidOperationSelectionException();
        }

        return ids.ToArray();
    }

    private static TrashEntry ToEntry(ValidTrashRecord record) => new(
        record.Manifest.TrashId,
        record.Manifest.SourceId,
        record.Manifest.OriginalLogicalPath,
        record.Manifest.OriginalName,
        record.Manifest.Type,
        record.Manifest.Size,
        record.Manifest.DeletedAt);

    private static long? SumBytes(IEnumerable<PlannedFileOperationEntry> entries)
    {
        long total = 0;
        foreach (var entry in entries)
        {
            if (entry.Fingerprint.Type == FileEntryType.File && entry.Fingerprint.Length is null)
            {
                return null;
            }

            total = checked(total + (entry.Fingerprint.Length ?? 0));
        }

        return total;
    }

    private static long? SumManifestBytes(IEnumerable<ValidTrashRecord> records)
    {
        long total = 0;
        foreach (var record in records)
        {
            if (record.Manifest.Type == FileEntryType.File && record.Manifest.Size is null)
            {
                return null;
            }

            total = checked(total + (record.Manifest.Size ?? 0));
        }

        return total;
    }

    private static bool IsDescendant(string ancestor, string candidate) =>
        candidate.StartsWith($"{ancestor}/", StringComparison.OrdinalIgnoreCase);

    private static string Parent(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator <= 0 ? "/" : path[..separator];
    }
}

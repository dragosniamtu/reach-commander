using ReachCommander.Application.BatchRenames;
using ReachCommander.Application.Files;
using ReachCommander.Domain.Files;

namespace ReachCommander.Infrastructure.BatchRenames;

internal sealed class BatchRenamePlanner
{
    private const int DefaultMaximumEntries = 5_000;
    private static readonly TimeSpan PlanLifetime = TimeSpan.FromMinutes(10);
    private readonly IPathSecurityService _pathSecurity;
    private readonly IBatchRenameFileSystem _fileSystem;
    private readonly RenameRuleEvaluator _ruleEvaluator;
    private readonly RenameNameValidator _nameValidator;
    private readonly BatchRenamePlanStore _planStore;
    private readonly TimeProvider _clock;
    private readonly int _maximumEntries;

    public BatchRenamePlanner(
        IPathSecurityService pathSecurity,
        IBatchRenameFileSystem fileSystem,
        RenameRuleEvaluator ruleEvaluator,
        RenameNameValidator nameValidator,
        BatchRenamePlanStore planStore,
        TimeProvider clock)
        : this(
            pathSecurity,
            fileSystem,
            ruleEvaluator,
            nameValidator,
            planStore,
            clock,
            DefaultMaximumEntries)
    {
    }

    internal BatchRenamePlanner(
        IPathSecurityService pathSecurity,
        IBatchRenameFileSystem fileSystem,
        RenameRuleEvaluator ruleEvaluator,
        RenameNameValidator nameValidator,
        BatchRenamePlanStore planStore,
        TimeProvider clock,
        int maximumEntries)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEntries, 1);
        _pathSecurity = pathSecurity;
        _fileSystem = fileSystem;
        _ruleEvaluator = ruleEvaluator;
        _nameValidator = nameValidator;
        _planStore = planStore;
        _clock = clock;
        _maximumEntries = maximumEntries;
    }

    public async ValueTask<BatchRenamePreview> PreviewAsync(
        BatchRenamePreviewCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateBatchSize(command.EntryPaths);
        var directory = await ResolveWritableDirectoryAsync(
            command.SourceId,
            command.DirectoryPath,
            cancellationToken);
        var entries = await ResolveSelectedEntriesAsync(directory, command.EntryPaths, cancellationToken);
        var directoryChildren = _fileSystem.ListChildren(
            directory.LogicalPath,
            directory.PhysicalPath);

        var candidates = new List<RenameCandidate>(entries.Count);
        for (var index = 0; index < entries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = entries[index];
            var evaluated = _ruleEvaluator.Evaluate(
                entry.Name,
                entry.Extension,
                entry.Type,
                command.Rules,
                index);
            var validation = _nameValidator.Validate(evaluated.CompleteName);
            var status = BatchRenamePreviewStatus.Ready;
            string? message = null;

            if (entry.IsSymbolicLink || entry.Type is not (FileEntryType.File or FileEntryType.Directory))
            {
                status = BatchRenamePreviewStatus.Invalid;
                message = "Symbolic links and unsupported entry types cannot be renamed.";
            }
            else if (!validation.IsValid)
            {
                status = BatchRenamePreviewStatus.Invalid;
                message = validation.Message;
            }
            else if (entry.Name.Equals(evaluated.CompleteName, StringComparison.Ordinal))
            {
                status = BatchRenamePreviewStatus.Unchanged;
            }

            ResolvedSourcePath? destination = null;
            if (validation.IsValid)
            {
                destination = await _pathSecurity.ResolveChildAsync(
                    directory.Source.Id,
                    directory.LogicalPath,
                    evaluated.CompleteName,
                    cancellationToken);
            }

            candidates.Add(new RenameCandidate(
                entry,
                evaluated.CompleteName,
                destination,
                status,
                message));
        }

        MarkDuplicateDestinations(candidates);
        MarkOccupiedDestinations(candidates, directoryChildren);

        var now = _clock.GetUtcNow();
        var planId = Guid.NewGuid();
        var expiresAt = now.Add(PlanLifetime);
        var plannedEntries = candidates.Select(candidate => candidate.ToPlannedRename()).ToArray();
        var rows = candidates.Select(candidate => candidate.ToPreviewRow()).ToArray();
        var changedCount = rows.Count(row => row.Status == BatchRenamePreviewStatus.Ready);
        var unchangedCount = rows.Count(row => row.Status == BatchRenamePreviewStatus.Unchanged);
        var invalidCount = rows.Length - changedCount - unchangedCount;
        var preview = new BatchRenamePreview(
            planId,
            expiresAt,
            rows,
            changedCount > 0 && invalidCount == 0,
            changedCount,
            unchangedCount,
            invalidCount);
        _planStore.AddPlan(new StoredBatchRenamePlan(
            planId,
            now,
            expiresAt,
            directory.Source.Id,
            directory.LogicalPath,
            directory.PhysicalPath,
            plannedEntries,
            preview));

        return preview;
    }

    public async ValueTask RevalidateAsync(
        StoredBatchRenamePlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var directory = await ResolveWritableDirectoryAsync(
            plan.SourceId,
            plan.DirectoryLogicalPath,
            cancellationToken);
        if (!PhysicalPathsEqual(directory.PhysicalPath, plan.DirectoryPhysicalPath))
        {
            throw Stale();
        }

        foreach (var planned in plan.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = NormalizeLogicalPath(planned.OldLogicalPath);
            RequireDirectChild(directory.LogicalPath, normalized);
            var childName = ChildName(normalized);
            var resolved = await _pathSecurity.ResolveChildAsync(
                plan.SourceId,
                directory.LogicalPath,
                childName,
                cancellationToken);
            if (!PhysicalPathsEqual(resolved.PhysicalPath, planned.OldPhysicalPath))
            {
                throw Stale();
            }

            var current = _fileSystem.GetEntry(resolved.LogicalPath, resolved.PhysicalPath);
            if (current.IsSymbolicLink ||
                current.Type is not (FileEntryType.File or FileEntryType.Directory) ||
                current.Fingerprint != planned.PreviewFingerprint)
            {
                throw Stale();
            }

            if (planned.Status == BatchRenamePreviewStatus.Ready)
            {
                var destination = await _pathSecurity.ResolveChildAsync(
                    plan.SourceId,
                    directory.LogicalPath,
                    planned.NewName,
                    cancellationToken);
                if (!PhysicalPathsEqual(destination.PhysicalPath, planned.NewPhysicalPath) ||
                    !destination.LogicalPath.Equals(planned.NewLogicalPath, StringComparison.Ordinal))
                {
                    throw Stale();
                }
            }
        }

        var ready = plan.Entries
            .Where(entry => entry.Status == BatchRenamePreviewStatus.Ready)
            .ToArray();
        if (ready.GroupBy(entry => entry.NewName, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
        {
            throw Stale();
        }

        var children = _fileSystem.ListChildren(directory.LogicalPath, directory.PhysicalPath);
        var movingSources = ready.Select(entry => entry.OldLogicalPath).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in ready)
        {
            if (children.Any(child =>
                    child.Name.Equals(entry.NewName, StringComparison.OrdinalIgnoreCase) &&
                    !movingSources.Contains(child.LogicalPath) &&
                    !child.LogicalPath.Equals(entry.OldLogicalPath, StringComparison.Ordinal)))
            {
                throw Stale();
            }
        }
    }

    private async ValueTask<ResolvedSourcePath> ResolveWritableDirectoryAsync(
        string sourceId,
        string logicalPath,
        CancellationToken cancellationToken)
    {
        var directory = await _pathSecurity.ResolveAsync(sourceId, logicalPath, cancellationToken);
        if (directory.Source.IsReadOnly)
        {
            throw new SourceReadOnlyException($"Source '{directory.Source.Id}' is read-only.");
        }

        if (!Directory.Exists(directory.PhysicalPath))
        {
            throw new InvalidLogicalPathException(
                directory.LogicalPath,
                "the selected entry is not a directory");
        }

        return directory;
    }

    private async ValueTask<IReadOnlyList<BatchRenameEntrySnapshot>> ResolveSelectedEntriesAsync(
        ResolvedSourcePath directory,
        IReadOnlyList<string> entryPaths,
        CancellationToken cancellationToken)
    {
        var normalizedPaths = new HashSet<string>(StringComparer.Ordinal);
        var physicalPaths = new HashSet<string>(PhysicalPathComparer());
        var entries = new List<BatchRenameEntrySnapshot>(entryPaths.Count);
        foreach (var entryPath in entryPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = NormalizeLogicalPath(entryPath);
            RequireDirectChild(directory.LogicalPath, normalized);
            if (!normalizedPaths.Add(normalized))
            {
                throw new InvalidLogicalPathException(entryPath, "selected entries must be distinct");
            }

            var resolved = await _pathSecurity.ResolveChildAsync(
                directory.Source.Id,
                directory.LogicalPath,
                ChildName(normalized),
                cancellationToken);
            if (!resolved.LogicalPath.Equals(normalized, StringComparison.Ordinal) ||
                !physicalPaths.Add(Path.GetFullPath(resolved.PhysicalPath)))
            {
                throw new InvalidLogicalPathException(entryPath, "selected entries must be distinct direct children");
            }

            entries.Add(_fileSystem.GetEntry(resolved.LogicalPath, resolved.PhysicalPath));
        }

        return entries;
    }

    private void ValidateBatchSize(IReadOnlyList<string>? entryPaths)
    {
        if (entryPaths is null || entryPaths.Count == 0)
        {
            throw new BatchTooLargeException("A rename preview requires at least one entry.");
        }

        if (entryPaths.Count > _maximumEntries)
        {
            throw new BatchTooLargeException($"A rename preview cannot exceed {_maximumEntries} entries.");
        }
    }

    private static void MarkDuplicateDestinations(IReadOnlyList<RenameCandidate> candidates)
    {
        foreach (var group in candidates
                     .Where(candidate => candidate.Status == BatchRenamePreviewStatus.Ready)
                     .GroupBy(candidate => candidate.NewName, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            foreach (var candidate in group)
            {
                candidate.MarkConflict("More than one entry has the same destination name.");
            }
        }
    }

    private static void MarkOccupiedDestinations(
        IReadOnlyList<RenameCandidate> candidates,
        IReadOnlyList<BatchRenameEntrySnapshot> directoryChildren)
    {
        var movingSources = candidates
            .Where(candidate => candidate.Status == BatchRenamePreviewStatus.Ready)
            .Select(candidate => candidate.Entry.LogicalPath)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var candidate in candidates.Where(candidate =>
                     candidate.Status == BatchRenamePreviewStatus.Ready))
        {
            var occupiedByUnselectedEntry = directoryChildren.Any(child =>
                child.Name.Equals(candidate.NewName, StringComparison.OrdinalIgnoreCase) &&
                !movingSources.Contains(child.LogicalPath) &&
                !child.LogicalPath.Equals(candidate.Entry.LogicalPath, StringComparison.Ordinal));
            if (occupiedByUnselectedEntry)
            {
                candidate.MarkConflict("The destination name is already in use.");
            }
        }
    }

    private static string NormalizeLogicalPath(string logicalPath)
    {
        if (string.IsNullOrEmpty(logicalPath) ||
            !logicalPath.StartsWith("/", StringComparison.Ordinal) ||
            logicalPath.StartsWith("//", StringComparison.Ordinal) ||
            logicalPath.Contains('\\') ||
            logicalPath.Contains('\0'))
        {
            throw new InvalidLogicalPathException(logicalPath, "it must be a source-relative path");
        }

        var segments = logicalPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var normalized = new List<string>(segments.Length);
        foreach (var segment in segments)
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                throw new InvalidLogicalPathException(logicalPath, "parent traversal is not allowed");
            }

            normalized.Add(segment);
        }

        return normalized.Count == 0 ? "/" : $"/{string.Join('/', normalized)}";
    }

    private static void RequireDirectChild(string parent, string child)
    {
        var separator = child.LastIndexOf('/');
        var childParent = separator <= 0 ? "/" : child[..separator];
        if (!childParent.Equals(parent, StringComparison.Ordinal) || child == "/")
        {
            throw new InvalidLogicalPathException(child, "the entry must be a direct child of the selected directory");
        }
    }

    private static string ChildName(string logicalPath) =>
        logicalPath[(logicalPath.LastIndexOf('/') + 1)..];

    private static StringComparer PhysicalPathComparer() => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static bool PhysicalPathsEqual(string left, string right) =>
        PhysicalPathComparer().Equals(Path.GetFullPath(left), Path.GetFullPath(right));

    private static RenamePlanStaleException Stale() =>
        new("The rename preview is stale. Refresh it before trying again.");

    private sealed class RenameCandidate(
        BatchRenameEntrySnapshot entry,
        string newName,
        ResolvedSourcePath? destination,
        BatchRenamePreviewStatus status,
        string? message)
    {
        public BatchRenameEntrySnapshot Entry { get; } = entry;

        public string NewName { get; } = newName;

        public ResolvedSourcePath? Destination { get; } = destination;

        public BatchRenamePreviewStatus Status { get; private set; } = status;

        public string? Message { get; private set; } = message;

        public void MarkConflict(string conflictMessage)
        {
            Status = BatchRenamePreviewStatus.Conflict;
            Message = conflictMessage;
        }

        public PlannedRename ToPlannedRename() => new(
            Entry.LogicalPath,
            Destination?.LogicalPath ?? string.Empty,
            Entry.PhysicalPath,
            Destination?.PhysicalPath ?? string.Empty,
            Entry.Name,
            NewName,
            Entry.Type,
            Entry.Fingerprint,
            Status,
            Message);

        public BatchRenamePreviewRow ToPreviewRow() => new(
            Entry.LogicalPath,
            Entry.Name,
            Entry.Extension,
            NewName,
            Entry.Type,
            Entry.Length,
            Entry.ModifiedAt,
            Status,
            Message);
    }
}

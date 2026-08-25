using ReachCommander.Application.FileOperations;
using ReachCommander.Infrastructure.FileOperations.Planning;

namespace ReachCommander.Infrastructure.FileOperations.Persistence;

internal sealed class FileOperationRepository(
    FileOperationDataPaths paths,
    TimeProvider clock)
{
    private const int MaximumTerminalOperations = 100;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, PersistedFileOperationDocument> _operations = new();
    private bool _loaded;
    private long _nextSequence;

    internal async Task<FileOperationStatus> EnqueueAsync(
        FileOperationPlan plan,
        FileOperationSubmissionApproval approval,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(approval);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            var now = clock.GetUtcNow();
            var operationId = Guid.NewGuid();
            var status = new FileOperationStatus(
                operationId,
                plan.Kind,
                FileOperationPhase.Queued,
                QueueCount() + 1,
                now,
                now,
                new FileOperationProgress(
                    null,
                    0,
                    plan.Entries.Count,
                    0,
                    plan.TotalBytes,
                    plan.TotalBytes is null ? null : 0,
                    null,
                    TimeSpan.Zero,
                    null),
                [],
                [],
                false);
            var document = new PersistedFileOperationDocument(
                FileOperationSchema.CurrentVersion,
                ++_nextSequence,
                plan,
                approval,
                status,
                false,
                null);
            await SaveAsync(document, cancellationToken);
            _operations.Add(operationId, document);
            return SnapshotStatus(document);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<PersistedFileOperationDocument?> TryTakeNextAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            if (_operations.Values.Any(document => document.Status.Phase is
                    FileOperationPhase.Validating or
                    FileOperationPhase.Running or
                    FileOperationPhase.Cancelling))
            {
                return null;
            }

            var next = _operations.Values
                .Where(document => document.Status.Phase == FileOperationPhase.Queued)
                .OrderBy(document => document.Sequence)
                .FirstOrDefault();
            if (next is null)
            {
                return null;
            }

            var claimed = next with
            {
                Status = next.Status with
                {
                    Phase = FileOperationPhase.Validating,
                    QueuePosition = 0,
                    UpdatedAt = clock.GetUtcNow(),
                },
            };
            await SaveAsync(claimed, cancellationToken);
            _operations[claimed.OperationId] = claimed;
            return claimed;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<PersistedFileOperationDocument> UpdateAsync(
        Guid operationId,
        Func<PersistedFileOperationDocument, PersistedFileOperationDocument> update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            var current = GetDocument(operationId);
            var candidate = update(current);
            var updated = candidate with
            {
                Status = candidate.Status with { UpdatedAt = clock.GetUtcNow() },
            };
            ValidateUpdate(current, updated);
            await SaveAsync(updated, cancellationToken);
            _operations[operationId] = updated;
            if (IsTerminal(updated.Status.Phase))
            {
                await TrimTerminalHistoryAsync(cancellationToken);
            }

            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<FileOperationStatus> RequestCancellationAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            var current = GetDocument(operationId);
            PersistedFileOperationDocument updated;
            if (current.Status.Phase == FileOperationPhase.Queued)
            {
                updated = current with
                {
                    CancellationRequested = true,
                    Status = current.Status with
                    {
                        Phase = FileOperationPhase.Cancelled,
                        QueuePosition = 0,
                        UpdatedAt = clock.GetUtcNow(),
                    },
                };
            }
            else if (current.Status.Phase is FileOperationPhase.Validating or FileOperationPhase.Running)
            {
                updated = current with
                {
                    CancellationRequested = true,
                    Status = current.Status with
                    {
                        Phase = FileOperationPhase.Cancelling,
                        UpdatedAt = clock.GetUtcNow(),
                    },
                };
            }
            else
            {
                return SnapshotStatus(current);
            }

            ValidateUpdate(current, updated);
            await SaveAsync(updated, cancellationToken);
            _operations[operationId] = updated;
            if (IsTerminal(updated.Status.Phase))
            {
                await TrimTerminalHistoryAsync(cancellationToken);
            }

            return SnapshotStatus(updated);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task RecoverAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            foreach (var current in _operations.Values
                         .Where(document => document.Status.Phase is
                             FileOperationPhase.Validating or
                             FileOperationPhase.Running or
                             FileOperationPhase.Cancelling)
                         .ToArray())
            {
                var warnings = current.Status.Warnings
                    .Append("The operation was interrupted by a server restart.")
                    .ToArray();
                var updated = current with
                {
                    CancellationRequested = false,
                    Status = current.Status with
                    {
                        Phase = FileOperationPhase.Interrupted,
                        QueuePosition = 0,
                        UpdatedAt = clock.GetUtcNow(),
                        Warnings = warnings,
                    },
                };
                await SaveAsync(updated, cancellationToken);
                _operations[current.OperationId] = updated;
            }

            await TrimTerminalHistoryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<IReadOnlyList<FileOperationStatus>> ListAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            return _operations.Values
                .OrderByDescending(document => document.Status.CreatedAt)
                .ThenByDescending(document => document.Sequence)
                .Select(SnapshotStatus)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<FileOperationStatus> GetAsync(
        Guid operationId,
        CancellationToken cancellationToken) =>
        await WithGateAsync(
            () => Task.FromResult(SnapshotStatus(GetDocument(operationId))),
            cancellationToken,
            ensureLoaded: true);

    internal async Task<PersistedFileOperationDocument> GetDocumentAsync(
        Guid operationId,
        CancellationToken cancellationToken) =>
        await WithGateAsync(
            () => Task.FromResult(GetDocument(operationId)),
            cancellationToken,
            ensureLoaded: true);

    internal async Task AcknowledgeAsync(Guid operationId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            var current = GetDocument(operationId);
            if (!IsTerminal(current.Status.Phase))
            {
                throw new InvalidOperationException("Only terminal operations can be acknowledged.");
            }

            var updated = current with
            {
                Status = current.Status with
                {
                    Acknowledged = true,
                    UpdatedAt = clock.GetUtcNow(),
                },
            };
            await SaveAsync(updated, cancellationToken);
            _operations[operationId] = updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<T> WithGateAsync<T>(
        Func<Task<T>> action,
        CancellationToken cancellationToken,
        bool ensureLoaded)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (ensureLoaded)
            {
                await EnsureLoadedAsync(cancellationToken);
            }

            return await action();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }

        paths.EnsureDirectories();
        foreach (var path in Directory.EnumerateFiles(paths.OperationsDirectory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = await AtomicJsonFile.ReadAsync<PersistedFileOperationDocument>(
                path,
                cancellationToken);
            if (document.SchemaVersion != FileOperationSchema.CurrentVersion ||
                document.OperationId == Guid.Empty ||
                document.Plan.PlanId == Guid.Empty)
            {
                throw new InvalidDataException("A persisted file operation schema is invalid.");
            }

            _operations.Add(document.OperationId, document);
            _nextSequence = Math.Max(_nextSequence, document.Sequence);
        }

        _loaded = true;
    }

    private async Task SaveAsync(
        PersistedFileOperationDocument document,
        CancellationToken cancellationToken) =>
        await AtomicJsonFile.WriteAsync(
            paths.OperationPath(document.OperationId),
            document,
            cancellationToken);

    private PersistedFileOperationDocument GetDocument(Guid operationId) =>
        _operations.TryGetValue(operationId, out var document)
            ? document
            : throw new OperationPlanNotFoundException();

    private FileOperationStatus SnapshotStatus(PersistedFileOperationDocument document)
    {
        var queuePosition = document.Status.Phase == FileOperationPhase.Queued
            ? _operations.Values
                .Where(candidate => candidate.Status.Phase == FileOperationPhase.Queued &&
                    candidate.Sequence <= document.Sequence)
                .Count()
            : 0;
        return document.Status with { QueuePosition = queuePosition };
    }

    private int QueueCount() =>
        _operations.Values.Count(document => document.Status.Phase == FileOperationPhase.Queued);

    private async Task TrimTerminalHistoryAsync(CancellationToken cancellationToken)
    {
        var remove = _operations.Values
            .Where(document => IsTerminal(document.Status.Phase))
            .OrderByDescending(document => document.Status.UpdatedAt)
            .ThenByDescending(document => document.Sequence)
            .Skip(MaximumTerminalOperations)
            .ToArray();
        foreach (var document in remove)
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(paths.OperationPath(document.OperationId));
            _operations.Remove(document.OperationId);
        }

        await Task.CompletedTask;
    }

    private static void ValidateUpdate(
        PersistedFileOperationDocument current,
        PersistedFileOperationDocument updated)
    {
        if (updated.OperationId != current.OperationId ||
            updated.Sequence != current.Sequence ||
            updated.Plan != current.Plan ||
            updated.Approval != current.Approval ||
            !CanTransition(current.Status.Phase, updated.Status.Phase) ||
            updated.Status.Progress.CompletedItems < current.Status.Progress.CompletedItems ||
            updated.Status.Progress.CompletedBytes < current.Status.Progress.CompletedBytes)
        {
            throw new InvalidOperationException("The file operation update is not monotonic.");
        }
    }

    private static bool CanTransition(FileOperationPhase current, FileOperationPhase next) =>
        current == next ||
        (current, next) switch
        {
            (FileOperationPhase.Queued, FileOperationPhase.Validating or FileOperationPhase.Cancelled) => true,
            (FileOperationPhase.Validating, FileOperationPhase.Running or FileOperationPhase.Cancelling or FileOperationPhase.Failed or FileOperationPhase.Interrupted) => true,
            (FileOperationPhase.Running, FileOperationPhase.Cancelling or FileOperationPhase.Completed or FileOperationPhase.CompletedWithErrors or FileOperationPhase.Cancelled or FileOperationPhase.Failed or FileOperationPhase.Interrupted) => true,
            (FileOperationPhase.Cancelling, FileOperationPhase.Cancelled or FileOperationPhase.CompletedWithErrors or FileOperationPhase.Failed or FileOperationPhase.Interrupted) => true,
            _ => false,
        };

    private static bool IsTerminal(FileOperationPhase phase) => phase is
        FileOperationPhase.Completed or
        FileOperationPhase.CompletedWithErrors or
        FileOperationPhase.Cancelled or
        FileOperationPhase.Failed or
        FileOperationPhase.Interrupted;
}

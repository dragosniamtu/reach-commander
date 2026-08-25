using ReachCommander.Application.Archives;

namespace ReachCommander.Infrastructure.Archives.Extraction;

internal sealed class ArchiveExtractionOperationStore(TimeProvider clock)
{
    private const int MaximumTerminalOperations = 100;
    private static readonly TimeSpan TerminalLifetime = TimeSpan.FromHours(1);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private long _terminalSequence;

    public ArchiveExtractionOperation Create(
        string operationId,
        ArchiveExtractionPlan plan)
    {
        lock (_gate)
        {
            PruneTerminal();
            if (_entries.ContainsKey(operationId))
            {
                throw new InvalidOperationException("An archive extraction operation ID was reused.");
            }

            var totalBytes = SumKnownSizes(plan.Files);
            var entry = new Entry(
                operationId,
                plan.Files.Count,
                totalBytes,
                clock.GetUtcNow(),
                plan.ExpiresAt);
            _entries.Add(operationId, entry);
            return Snapshot(entry);
        }
    }

    public ArchiveExtractionOperation GetRequired(string operationId)
    {
        lock (_gate)
        {
            PruneTerminal();
            return Snapshot(GetEntry(operationId));
        }
    }

    public bool Contains(string operationId)
    {
        lock (_gate)
        {
            PruneTerminal();
            return _entries.ContainsKey(operationId);
        }
    }

    public bool HasActiveOperations()
    {
        lock (_gate)
        {
            PruneTerminal();
            return _entries.Values.Any(entry => !IsTerminal(entry.State));
        }
    }

    public CancellationToken GetCancellationToken(string operationId)
    {
        lock (_gate)
        {
            return GetEntry(operationId).Cancellation.Token;
        }
    }

    public void MarkExtracting(string operationId)
    {
        lock (_gate)
        {
            Transition(GetEntry(operationId), ArchiveExtractionState.Extracting);
        }
    }

    public void ReportProgress(
        string operationId,
        int completedFiles,
        long extractedBytes,
        string? currentEntryName)
    {
        lock (_gate)
        {
            var entry = GetEntry(operationId);
            if (entry.State != ArchiveExtractionState.Extracting ||
                completedFiles < entry.CompletedFiles ||
                extractedBytes < entry.ExtractedBytes ||
                completedFiles > entry.TotalFiles ||
                extractedBytes < 0)
            {
                throw new InvalidOperationException("Archive extraction progress must be monotonic.");
            }

            entry.CompletedFiles = completedFiles;
            entry.ExtractedBytes = extractedBytes;
            entry.CurrentEntryName = SafeName(currentEntryName);
        }
    }

    public void MarkFinalizing(string operationId)
    {
        lock (_gate)
        {
            var entry = GetEntry(operationId);
            Transition(entry, ArchiveExtractionState.Finalizing);
            entry.CurrentEntryName = null;
        }
    }

    public void MarkCompleted(string operationId)
    {
        lock (_gate)
        {
            var entry = GetEntry(operationId);
            entry.CompletedFiles = entry.TotalFiles;
            TransitionTerminal(entry, ArchiveExtractionState.Completed);
        }
    }

    public void MarkCancelled(string operationId)
    {
        lock (_gate)
        {
            TransitionTerminal(GetEntry(operationId), ArchiveExtractionState.Cancelled);
        }
    }

    public void MarkFailed(
        string operationId,
        ArchiveException exception,
        ArchiveCompensationState compensationState = ArchiveCompensationState.NotRequired)
    {
        lock (_gate)
        {
            var entry = GetEntry(operationId);
            if (IsTerminal(entry.State))
            {
                return;
            }

            entry.ErrorCode = SafeText(exception.Code);
            entry.ErrorDetail = SafeText(exception.Detail);
            entry.CompensationState = compensationState;
            TransitionTerminal(entry, ArchiveExtractionState.Failed);
        }
    }

    public void MarkRecoveryRequired(
        string operationId,
        IEnumerable<string> recoveryNames)
    {
        lock (_gate)
        {
            var entry = GetEntry(operationId);
            if (IsTerminal(entry.State))
            {
                return;
            }

            var exception = new ArchiveRecoveryRequiredException(recoveryNames);
            entry.ErrorCode = exception.Code;
            entry.ErrorDetail = exception.Detail;
            entry.CompensationState = ArchiveCompensationState.Failed;
            entry.RecoveryNames = Array.AsReadOnly(recoveryNames
                .Take(100)
                .Select(SafeName)
                .Where(name => name is not null)
                .Cast<string>()
                .ToArray());
            TransitionTerminal(entry, ArchiveExtractionState.RecoveryRequired);
        }
    }

    public ArchiveExtractionOperation RequestCancellation(string operationId)
    {
        lock (_gate)
        {
            var entry = GetEntry(operationId);
            if (entry.State is ArchiveExtractionState.Queued or ArchiveExtractionState.Extracting)
            {
                entry.Cancellation.Cancel();
            }

            return Snapshot(entry);
        }
    }

    public async ValueTask<ArchiveExtractionOperation> WaitForTerminalAsync(
        string operationId,
        CancellationToken cancellationToken)
    {
        Task completion;
        lock (_gate)
        {
            var entry = GetEntry(operationId);
            if (IsTerminal(entry.State))
            {
                return Snapshot(entry);
            }

            completion = entry.Terminal.Task;
        }

        await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        return GetRequired(operationId);
    }

    internal IReadOnlyList<ArchiveExtractionState> GetStateHistory(string operationId)
    {
        lock (_gate)
        {
            return Array.AsReadOnly(GetEntry(operationId).StateHistory.ToArray());
        }
    }

    private void Transition(Entry entry, ArchiveExtractionState state)
    {
        if (IsTerminal(entry.State))
        {
            throw new InvalidOperationException("A terminal archive operation cannot transition.");
        }

        entry.State = state;
        entry.StateHistory.Add(state);
    }

    private void TransitionTerminal(Entry entry, ArchiveExtractionState state)
    {
        if (IsTerminal(entry.State))
        {
            return;
        }

        entry.State = state;
        entry.CurrentEntryName = null;
        entry.TerminalAt = clock.GetUtcNow();
        entry.TerminalSequence = ++_terminalSequence;
        entry.StateHistory.Add(state);
        entry.Terminal.TrySetResult();
    }

    private Entry GetEntry(string operationId) =>
        _entries.TryGetValue(operationId, out var entry)
            ? entry
            : throw new ArchivePlanNotFoundException();

    private void PruneTerminal()
    {
        var cutoff = clock.GetUtcNow() - TerminalLifetime;
        foreach (var entry in _entries.Values
                     .Where(entry =>
                         entry.TerminalAt <= cutoff &&
                         entry.RetainAtLeastUntil <= clock.GetUtcNow())
                     .ToArray())
        {
            entry.Cancellation.Dispose();
            _entries.Remove(entry.OperationId);
        }

        var terminal = _entries.Values
            .Where(entry => entry.TerminalAt is not null)
            .OrderByDescending(entry => entry.TerminalAt)
            .ThenByDescending(entry => entry.TerminalSequence)
            .Skip(MaximumTerminalOperations)
            .Where(entry => entry.RetainAtLeastUntil <= clock.GetUtcNow())
            .ToArray();
        foreach (var entry in terminal)
        {
            entry.Cancellation.Dispose();
            _entries.Remove(entry.OperationId);
        }
    }

    private static ArchiveExtractionOperation Snapshot(Entry entry) => new(
        entry.OperationId,
        entry.State,
        entry.CompletedFiles,
        entry.TotalFiles,
        entry.ExtractedBytes,
        entry.TotalBytes,
        CalculatePercent(entry),
        entry.CurrentEntryName,
        entry.State is ArchiveExtractionState.Queued or ArchiveExtractionState.Extracting,
        entry.CompensationState,
        entry.RecoveryNames,
        entry.ErrorCode,
        entry.ErrorDetail);

    private static double? CalculatePercent(Entry entry)
    {
        if (entry.TotalBytes is null)
        {
            return null;
        }

        if (entry.TotalBytes == 0)
        {
            return entry.State == ArchiveExtractionState.Completed ? 100 : 0;
        }

        return Math.Min(100, entry.ExtractedBytes * 100d / entry.TotalBytes.Value);
    }

    private static long? SumKnownSizes(IReadOnlyList<PlannedArchiveFile> files)
    {
        long total = 0;
        foreach (var file in files)
        {
            if (file.DeclaredSize is null)
            {
                return null;
            }

            total = checked(total + file.DeclaredSize.Value);
        }

        return total;
    }

    private static bool IsTerminal(ArchiveExtractionState state) => state is
        ArchiveExtractionState.Completed or
        ArchiveExtractionState.Cancelled or
        ArchiveExtractionState.Failed or
        ArchiveExtractionState.RecoveryRequired;

    private static string SafeText(string value) =>
        new(value.Where(character => !char.IsControl(character)).Take(512).ToArray());

    private static string? SafeName(string? value) =>
        value is null ? null : SafeText(Path.GetFileName(value));

    private sealed class Entry(
        string operationId,
        int totalFiles,
        long? totalBytes,
        DateTimeOffset createdAt,
        DateTimeOffset retainAtLeastUntil)
    {
        public string OperationId { get; } = operationId;
        public int TotalFiles { get; } = totalFiles;
        public long? TotalBytes { get; } = totalBytes;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public DateTimeOffset RetainAtLeastUntil { get; } = retainAtLeastUntil;
        public ArchiveExtractionState State { get; set; } = ArchiveExtractionState.Queued;
        public int CompletedFiles { get; set; }
        public long ExtractedBytes { get; set; }
        public string? CurrentEntryName { get; set; }
        public ArchiveCompensationState CompensationState { get; set; } =
            ArchiveCompensationState.NotRequired;
        public IReadOnlyList<string> RecoveryNames { get; set; } = Array.Empty<string>();
        public string? ErrorCode { get; set; }
        public string? ErrorDetail { get; set; }
        public DateTimeOffset? TerminalAt { get; set; }
        public long TerminalSequence { get; set; }
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource Terminal { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public List<ArchiveExtractionState> StateHistory { get; } = [ArchiveExtractionState.Queued];
    }
}

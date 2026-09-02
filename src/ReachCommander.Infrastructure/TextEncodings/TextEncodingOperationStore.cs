using System.Text;
using ReachCommander.Application.TextEncodings;

namespace ReachCommander.Infrastructure.TextEncodings;

internal sealed class TextEncodingOperationStore(TimeProvider clock)
{
    private const int MaximumTerminalOperations = 100;
    private const int MaximumCurrentFileNameScalars = 160;
    private static readonly TimeSpan TerminalLifetime = TimeSpan.FromHours(1);
    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly object _gate = new();
    private long _terminalSequence;

    public TextEncodingOperation Create(
        Guid operationId,
        IReadOnlyList<StoredTextEncodingEntry> plannedEntries)
    {
        ArgumentNullException.ThrowIfNull(plannedEntries);
        lock (_gate)
        {
            RemoveExpired(clock.GetUtcNow());
            if (_entries.TryGetValue(operationId, out var existing))
            {
                return Snapshot(existing);
            }

            var entry = new Entry(
                operationId,
                plannedEntries.Select(planned => new TextEncodingOperationRow(
                    planned.LogicalPath,
                    BackupPath: null,
                    TextEncodingRowResult.Pending,
                    Code: null,
                    Detail: null)).ToArray());
            _entries.Add(operationId, entry);
            return Snapshot(entry);
        }
    }

    public TextEncodingOperation GetRequired(Guid operationId)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(operationId, out var entry))
            {
                RemoveExpired(clock.GetUtcNow());
                throw TextEncodingException.OperationNotFound();
            }

            var now = clock.GetUtcNow();
            if (entry.TerminalAt is { } terminalAt && terminalAt + TerminalLifetime <= now)
            {
                RemoveEntry(entry);
                RemoveExpired(now);
                throw TextEncodingException.OperationExpired();
            }

            RemoveExpired(now, operationId);
            return Snapshot(entry);
        }
    }

    public CancellationToken GetCancellationToken(Guid operationId)
    {
        lock (_gate)
        {
            return GetEntry(operationId).Cancellation.Token;
        }
    }

    public TextEncodingOperation MarkRunning(Guid operationId)
    {
        lock (_gate)
        {
            var entry = GetEntry(operationId);
            if (entry.State == TextEncodingOperationState.Queued)
            {
                entry.State = TextEncodingOperationState.Running;
            }

            return Snapshot(entry);
        }
    }

    public TextEncodingOperation BeginFile(Guid operationId, int rowIndex, string fileName)
    {
        lock (_gate)
        {
            var entry = GetEntry(operationId);
            ValidateRow(entry, rowIndex);
            if (entry.Rows[rowIndex].Result == TextEncodingRowResult.Pending &&
                entry.State is TextEncodingOperationState.Running or TextEncodingOperationState.CancelRequested)
            {
                entry.CurrentFileName = BoundFileName(fileName);
            }

            return Snapshot(entry);
        }
    }

    public TextEncodingOperation CompleteFile(
        Guid operationId,
        int rowIndex,
        TextEncodingRowResult result,
        string? backupPath,
        string? code,
        string? detail)
    {
        if (result == TextEncodingRowResult.Pending)
        {
            throw new ArgumentOutOfRangeException(nameof(result));
        }

        lock (_gate)
        {
            var entry = GetEntry(operationId);
            ValidateRow(entry, rowIndex);
            if (entry.Rows[rowIndex].Result != TextEncodingRowResult.Pending)
            {
                return Snapshot(entry);
            }

            entry.Rows[rowIndex] = entry.Rows[rowIndex] with
            {
                BackupPath = backupPath,
                Result = result,
                Code = code,
                Detail = detail,
            };
            entry.CompletedFiles++;
            entry.CurrentFileName = null;
            return Snapshot(entry);
        }
    }

    public TextEncodingOperation RequestCancellation(Guid operationId)
    {
        lock (_gate)
        {
            var entry = GetEntry(operationId);
            if (entry.State is TextEncodingOperationState.Queued or TextEncodingOperationState.Running)
            {
                entry.State = TextEncodingOperationState.CancelRequested;
                entry.Cancellation.Cancel();
            }

            return Snapshot(entry);
        }
    }

    public TextEncodingOperation MarkTerminal(
        Guid operationId,
        TextEncodingOperationState requestedState,
        string? errorCode = null,
        string? errorDetail = null)
    {
        lock (_gate)
        {
            var entry = GetEntry(operationId);
            if (IsTerminal(entry.State))
            {
                return Snapshot(entry);
            }

            if (entry.Rows.Any(row => row.Result == TextEncodingRowResult.RecoveryRequired))
            {
                entry.State = TextEncodingOperationState.Failed;
                entry.ErrorCode = "text_encoding_recovery_required";
                entry.ErrorDetail = "A file could not be restored automatically. Manual recovery is required.";
            }
            else if (requestedState == TextEncodingOperationState.Completed)
            {
                entry.State = entry.Rows.All(row => row.Result == TextEncodingRowResult.Converted)
                    ? TextEncodingOperationState.Completed
                    : TextEncodingOperationState.CompletedWithErrors;
            }
            else if (requestedState is TextEncodingOperationState.Cancelled or TextEncodingOperationState.Failed)
            {
                entry.State = requestedState;
                entry.ErrorCode = errorCode;
                entry.ErrorDetail = errorDetail;
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(requestedState));
            }

            entry.CurrentFileName = null;
            entry.TerminalAt = clock.GetUtcNow();
            entry.TerminalSequence = ++_terminalSequence;
            RemoveExpired(entry.TerminalAt.Value, operationId);
            TrimTerminalOperations();
            return Snapshot(entry);
        }
    }

    public bool HasActiveOperation()
    {
        lock (_gate)
        {
            RemoveExpired(clock.GetUtcNow());
            return _entries.Values.Any(entry => !IsTerminal(entry.State));
        }
    }

    private Entry GetEntry(Guid operationId)
    {
        if (!_entries.TryGetValue(operationId, out var entry))
        {
            throw TextEncodingException.OperationNotFound();
        }

        return entry;
    }

    private static TextEncodingOperation Snapshot(Entry entry)
    {
        var totalFiles = entry.Rows.Length;
        var percent = totalFiles == 0
            ? 100
            : Math.Clamp(entry.CompletedFiles * 100d / totalFiles, 0, 100);
        return new TextEncodingOperation(
            entry.OperationId,
            entry.State,
            entry.CompletedFiles,
            totalFiles,
            percent,
            entry.CurrentFileName,
            entry.State is TextEncodingOperationState.Queued or TextEncodingOperationState.Running,
            entry.Rows.ToArray(),
            entry.ErrorCode,
            entry.ErrorDetail);
    }

    private static void ValidateRow(Entry entry, int rowIndex)
    {
        if ((uint)rowIndex >= (uint)entry.Rows.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(rowIndex));
        }
    }

    private static bool IsTerminal(TextEncodingOperationState state) => state is
        TextEncodingOperationState.Completed or
        TextEncodingOperationState.CompletedWithErrors or
        TextEncodingOperationState.Cancelled or
        TextEncodingOperationState.Failed;

    private static string BoundFileName(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        var safeName = fileName.Replace('\\', '/').Split('/').Last();
        var builder = new StringBuilder();
        var count = 0;
        foreach (var rune in safeName.EnumerateRunes())
        {
            if (count++ >= MaximumCurrentFileNameScalars)
            {
                break;
            }

            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }

    private void RemoveExpired(DateTimeOffset now, Guid? exceptOperationId = null)
    {
        foreach (var entry in _entries.Values.ToArray())
        {
            if (entry.OperationId != exceptOperationId &&
                entry.TerminalAt is { } terminalAt &&
                terminalAt + TerminalLifetime <= now)
            {
                RemoveEntry(entry);
            }
        }
    }

    private void TrimTerminalOperations()
    {
        var terminalEntries = _entries.Values
            .Where(entry => entry.TerminalAt is not null)
            .OrderBy(entry => entry.TerminalSequence)
            .ToArray();
        for (var index = 0; index < terminalEntries.Length - MaximumTerminalOperations; index++)
        {
            RemoveEntry(terminalEntries[index]);
        }
    }

    private void RemoveEntry(Entry entry)
    {
        _entries.Remove(entry.OperationId);
        entry.Cancellation.Dispose();
    }

    private sealed class Entry(
        Guid operationId,
        TextEncodingOperationRow[] rows)
    {
        public Guid OperationId { get; } = operationId;

        public TextEncodingOperationState State { get; set; } = TextEncodingOperationState.Queued;

        public int CompletedFiles { get; set; }

        public string? CurrentFileName { get; set; }

        public TextEncodingOperationRow[] Rows { get; } = rows;

        public string? ErrorCode { get; set; }

        public string? ErrorDetail { get; set; }

        public DateTimeOffset? TerminalAt { get; set; }

        public long TerminalSequence { get; set; }

        public CancellationTokenSource Cancellation { get; } = new();
    }
}

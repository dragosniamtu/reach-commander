using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReachCommander.Application.Archives;
using ReachCommander.ArchiveProtocol;
using ReachCommander.Domain.Archives;
using ReachCommander.Infrastructure.Archives.Catalog;
using ReachCommander.Infrastructure.Archives.Volumes;

namespace ReachCommander.Infrastructure.Archives.Worker;

internal sealed class ArchiveWorkerClient(
    IArchiveWorkerProcessFactory processFactory,
    IArchiveWorkerDelay delay,
    IOptions<ArchiveOptions> options,
    ILogger<ArchiveWorkerClient>? logger = null) : IArchiveWorkerClient
{
    private const int MaximumStderrCaptureBytes = 16 * 1024;
    private const long MaximumInspectionOutputBytes = 512L * 1024 * 1024;
    private static readonly TimeSpan WorkingSetSampleInterval = TimeSpan.FromMilliseconds(250);
    private readonly ArchiveOptions _options = options.Value;
    private readonly ILogger<ArchiveWorkerClient>? _logger = logger;

    public async ValueTask<ArchiveWorkerInspection> InspectAsync(
        ResolvedArchivePartSet partSet,
        CancellationToken cancellationToken)
    {
        var startInfo = CreateStartInfo();
        IArchiveWorkerProcess process;
        try
        {
            process = processFactory.Start(startInfo);
        }
        catch
        {
            throw new ArchiveWorkerFailedException();
        }

        await using var processScope = process;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.InspectionTimeout);
        using var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            timeout.Token);
        var stderrTask = CaptureStderrAsync(process.StandardError, timeout.Token);

        try
        {
            var request = new ArchiveInspectionRequest(
                ArchiveFrameCodec.CurrentProtocolVersion,
                Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
                partSet.Parts.Select(part => part.PhysicalPath).ToArray(),
                new ArchiveWorkerLimits(
                    _options.MaxEntries,
                    _options.MaxTotalExtractedBytes));
            await ArchiveFrameCodec.WriteJsonAsync(
                process.StandardInput,
                ArchiveFrameKind.InspectionRequest,
                request,
                timeout.Token).ConfigureAwait(false);
            await process.CompleteInputAsync().ConfigureAwait(false);

            var readTask = ReadInspectionAsync(
                process.StandardOutput,
                partSet.Format,
                timeout.Token);
            var monitorTask = MonitorWorkingSetAsync(
                process,
                monitorCancellation.Token).AsTask();
            var completed = await Task.WhenAny(readTask, monitorTask).ConfigureAwait(false);
            if (completed == monitorTask)
            {
                await monitorTask.ConfigureAwait(false);
            }

            var inspection = await readTask.ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new ArchiveWorkerFailedException();
            }

            await EnsureEndOfOutputAsync(process.StandardOutput, timeout.Token).ConfigureAwait(false);
            monitorCancellation.Cancel();
            await IgnoreMonitorCancellationAsync(monitorTask).ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (stderr.Length > 0)
            {
                _logger?.LogDebug(
                    "Archive worker emitted a bounded diagnostic category ({Length} bytes).",
                    stderr.Length);
            }

            return inspection;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            timeout.Cancel();
            await TerminateAsync(process).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            timeout.Cancel();
            await TerminateAsync(process).ConfigureAwait(false);
            throw new ArchiveLimitExceededException(
                "Archive inspection exceeded the configured time limit.");
        }
        catch (ArchiveException)
        {
            timeout.Cancel();
            await TerminateAsync(process).ConfigureAwait(false);
            throw;
        }
        catch
        {
            timeout.Cancel();
            await TerminateAsync(process).ConfigureAwait(false);
            throw new ArchiveWorkerFailedException();
        }
        finally
        {
            monitorCancellation.Cancel();
            await IgnoreCaptureFailureAsync(stderrTask).ConfigureAwait(false);
        }
    }

    public async ValueTask ExtractAsync(
        ResolvedArchivePartSet partSet,
        IReadOnlyList<int> entryIndexes,
        IArchiveEntrySink sink,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(partSet);
        ArgumentNullException.ThrowIfNull(entryIndexes);
        ArgumentNullException.ThrowIfNull(sink);
        if (entryIndexes.Count == 0 ||
            entryIndexes.Any(index => index < 0) ||
            entryIndexes.Distinct().Count() != entryIndexes.Count)
        {
            throw new ArchiveWorkerFailedException();
        }

        var startInfo = CreateStartInfo();
        IArchiveWorkerProcess process;
        try
        {
            process = processFactory.Start(startInfo);
        }
        catch
        {
            throw new ArchiveWorkerFailedException();
        }

        await using var processScope = process;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.ExtractionTimeout);
        using var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var stderrTask = CaptureStderrAsync(process.StandardError, timeout.Token);

        try
        {
            var request = new ArchiveExtractionRequest(
                ArchiveFrameCodec.CurrentProtocolVersion,
                Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
                partSet.Parts.Select(part => part.PhysicalPath).ToArray(),
                entryIndexes,
                new ArchiveWorkerLimits(
                    _options.MaxEntries,
                    _options.MaxTotalExtractedBytes));
            await ArchiveFrameCodec.WriteJsonAsync(
                process.StandardInput,
                ArchiveFrameKind.ExtractionRequest,
                request,
                timeout.Token).ConfigureAwait(false);
            await process.CompleteInputAsync().ConfigureAwait(false);

            var readTask = ReadExtractionAsync(
                process.StandardOutput,
                entryIndexes,
                sink,
                timeout.Token);
            var monitorTask = MonitorWorkingSetAsync(
                process,
                monitorCancellation.Token).AsTask();
            var completed = await Task.WhenAny(readTask, monitorTask).ConfigureAwait(false);
            if (completed == monitorTask)
            {
                await monitorTask.ConfigureAwait(false);
            }

            await readTask.ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new ArchiveWorkerFailedException();
            }

            await EnsureEndOfOutputAsync(process.StandardOutput, timeout.Token).ConfigureAwait(false);
            monitorCancellation.Cancel();
            await IgnoreMonitorCancellationAsync(monitorTask).ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (stderr.Length > 0)
            {
                _logger?.LogDebug(
                    "Archive extraction worker emitted a bounded diagnostic category ({Length} bytes).",
                    stderr.Length);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            timeout.Cancel();
            await TerminateAsync(process).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            timeout.Cancel();
            await TerminateAsync(process).ConfigureAwait(false);
            throw new ArchiveLimitExceededException(
                "Archive extraction exceeded the configured time limit.");
        }
        catch (ArchiveException)
        {
            timeout.Cancel();
            await TerminateAsync(process).ConfigureAwait(false);
            throw;
        }
        catch
        {
            timeout.Cancel();
            await TerminateAsync(process).ConfigureAwait(false);
            throw new ArchiveWorkerFailedException();
        }
        finally
        {
            monitorCancellation.Cancel();
            await IgnoreCaptureFailureAsync(stderrTask).ConfigureAwait(false);
        }
    }

    private ProcessStartInfo CreateStartInfo()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(Path.Combine(
            AppContext.BaseDirectory,
            "archive-worker",
            "ReachCommander.ArchiveWorker.dll"));
        startInfo.Environment["DOTNET_GCHeapHardLimit"] =
            _options.WorkerManagedMemoryBytes.ToString("X", CultureInfo.InvariantCulture);
        return startInfo;
    }

    private async Task<ArchiveWorkerInspection> ReadInspectionAsync(
        Stream output,
        ArchiveFormat expectedFormat,
        CancellationToken cancellationToken)
    {
        long outputBytes = 0;
        var first = await ReadFrameAsync(output, cancellationToken).ConfigureAwait(false);
        outputBytes = AddOutputBytes(outputBytes, first.Payload.Length);
        if (first.Kind == ArchiveFrameKind.Failure)
        {
            throw MapFailure(first.Deserialize<ArchiveFailureFrame>().Code);
        }

        if (first.Kind != ArchiveFrameKind.ArchiveDetected)
        {
            throw new ArchiveWorkerFailedException();
        }

        var detected = first.Deserialize<ArchiveDetectedFrame>();
        var format = ParseFormat(detected.Format);
        if (format != expectedFormat)
        {
            throw new ArchiveWorkerFailedException();
        }

        var entries = new List<UntrustedArchiveEntry>();
        while (true)
        {
            var frame = await ReadFrameAsync(output, cancellationToken).ConfigureAwait(false);
            outputBytes = AddOutputBytes(outputBytes, frame.Payload.Length);
            if (frame.Kind == ArchiveFrameKind.InspectionCompleted)
            {
                if (!frame.Payload.IsEmpty)
                {
                    throw new ArchiveWorkerFailedException();
                }

                return new ArchiveWorkerInspection(
                    format,
                    detected.IsSolid,
                    entries.AsReadOnly());
            }

            if (frame.Kind != ArchiveFrameKind.ArchiveEntry ||
                entries.Count >= _options.MaxEntries)
            {
                throw new ArchiveWorkerFailedException();
            }

            var entry = frame.Deserialize<ArchiveEntryFrame>();
            if (entry.Index != entries.Count)
            {
                throw new ArchiveWorkerFailedException();
            }

            entries.Add(new UntrustedArchiveEntry(
                entry.Index,
                entry.Key,
                entry.IsDirectory,
                entry.IsEncrypted,
                entry.IsLink,
                entry.IsSpecial,
                entry.Size,
                entry.CompressedSize,
                entry.ModifiedAt));
        }
    }

    private static ValueTask<ArchiveFrame> ReadFrameAsync(
        Stream output,
        CancellationToken cancellationToken) =>
        ArchiveFrameCodec.ReadAsync(
            output,
            ArchiveFrameCodec.MaxJsonPayloadBytes,
            cancellationToken);

    private async Task ReadExtractionAsync(
        Stream output,
        IReadOnlyList<int> entryIndexes,
        IArchiveEntrySink sink,
        CancellationToken cancellationToken)
    {
        var expected = entryIndexes.ToHashSet();
        var seen = new HashSet<int>();
        int? currentIndex = null;
        long currentBytes = 0;
        long totalBytes = 0;
        var completedFiles = 0;
        var lastEntryIndex = -1;
        var progressRequired = false;

        while (true)
        {
            var frame = await ReadFrameAsync(output, cancellationToken).ConfigureAwait(false);
            if (frame.Kind == ArchiveFrameKind.Failure)
            {
                throw MapFailure(frame.Deserialize<ArchiveFailureFrame>().Code);
            }

            switch (frame.Kind)
            {
                case ArchiveFrameKind.EntryStart:
                {
                    if (currentIndex is not null || progressRequired)
                    {
                        throw new ArchiveWorkerFailedException();
                    }

                    var start = frame.Deserialize<ArchiveEntryStartFrame>();
                    if (!expected.Contains(start.Index) ||
                        !seen.Add(start.Index) ||
                        start.Index <= lastEntryIndex)
                    {
                        throw new ArchiveWorkerFailedException();
                    }

                    currentIndex = start.Index;
                    currentBytes = 0;
                    lastEntryIndex = start.Index;
                    await sink.StartAsync(start.Index, cancellationToken).ConfigureAwait(false);
                    break;
                }
                case ArchiveFrameKind.EntryData:
                {
                    if (currentIndex is null || progressRequired || frame.Payload.IsEmpty)
                    {
                        throw new ArchiveWorkerFailedException();
                    }

                    try
                    {
                        currentBytes = checked(currentBytes + frame.Payload.Length);
                        totalBytes = checked(totalBytes + frame.Payload.Length);
                    }
                    catch (OverflowException)
                    {
                        throw new ArchiveLimitExceededException(
                            "Archive extraction exceeded the configured size limit.");
                    }

                    if (totalBytes > _options.MaxTotalExtractedBytes)
                    {
                        throw new ArchiveLimitExceededException(
                            "Archive extraction exceeded the configured size limit.");
                    }

                    await sink.WriteAsync(frame.Payload, cancellationToken).ConfigureAwait(false);
                    break;
                }
                case ArchiveFrameKind.EntryEnd:
                {
                    var end = frame.Deserialize<ArchiveEntryEndFrame>();
                    if (currentIndex is null || progressRequired ||
                        end.Index != currentIndex ||
                        end.ActualBytes != currentBytes)
                    {
                        throw new ArchiveWorkerFailedException();
                    }

                    await sink.EndAsync(
                        end.Index,
                        end.ActualBytes,
                        cancellationToken).ConfigureAwait(false);
                    completedFiles++;
                    currentIndex = null;
                    progressRequired = true;
                    break;
                }
                case ArchiveFrameKind.Progress:
                {
                    var progress = frame.Deserialize<ArchiveProgressFrame>();
                    if (currentIndex is not null || !progressRequired ||
                        progress.CompletedFiles != completedFiles ||
                        progress.ActualBytes != totalBytes)
                    {
                        throw new ArchiveWorkerFailedException();
                    }

                    await sink.ProgressAsync(
                        progress.CompletedFiles,
                        progress.ActualBytes,
                        cancellationToken).ConfigureAwait(false);
                    progressRequired = false;
                    break;
                }
                case ArchiveFrameKind.Completed:
                {
                    var completed = frame.Deserialize<ArchiveCompletedFrame>();
                    if (currentIndex is not null || progressRequired ||
                        completed.CompletedFiles != completedFiles ||
                        completed.ActualBytes != totalBytes ||
                        completedFiles != expected.Count ||
                        seen.Count != expected.Count)
                    {
                        throw new ArchiveWorkerFailedException();
                    }

                    return;
                }
                default:
                    throw new ArchiveWorkerFailedException();
            }
        }
    }

    private async ValueTask MonitorWorkingSetAsync(
        IArchiveWorkerProcess process,
        CancellationToken cancellationToken)
    {
        while (!process.HasExited)
        {
            await delay.DelayAsync(
                WorkingSetSampleInterval,
                cancellationToken).ConfigureAwait(false);
            if (!process.HasExited && process.WorkingSetBytes > _options.WorkerWorkingSetBytes)
            {
                throw new ArchiveLimitExceededException(
                    "Archive inspection exceeded the configured memory limit.");
            }
        }
    }

    private static async Task<byte[]> CaptureStderrAsync(
        Stream stderr,
        CancellationToken cancellationToken)
    {
        using var captured = new MemoryStream(MaximumStderrCaptureBytes);
        var buffer = new byte[4096];
        while (true)
        {
            var read = await stderr.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return captured.ToArray();
            }

            var remaining = MaximumStderrCaptureBytes - (int)captured.Length;
            if (remaining > 0)
            {
                captured.Write(buffer, 0, Math.Min(read, remaining));
            }
        }
    }

    private static async ValueTask EnsureEndOfOutputAsync(
        Stream output,
        CancellationToken cancellationToken)
    {
        var trailing = new byte[1];
        if (await output.ReadAsync(trailing, cancellationToken).ConfigureAwait(false) != 0)
        {
            throw new ArchiveWorkerFailedException();
        }
    }

    private static long AddOutputBytes(long current, int payloadLength)
    {
        try
        {
            var updated = checked(current + ArchiveFrameCodec.HeaderLength + payloadLength);
            if (updated > MaximumInspectionOutputBytes)
            {
                throw new ArchiveLimitExceededException(
                    "Archive inspection exceeded the configured output limit.");
            }

            return updated;
        }
        catch (OverflowException)
        {
            throw new ArchiveLimitExceededException(
                "Archive inspection exceeded the configured output limit.");
        }
    }

    private static ArchiveFormat ParseFormat(string format) => format switch
    {
        "zip" => ArchiveFormat.Zip,
        "rar" => ArchiveFormat.Rar,
        "sevenZip" => ArchiveFormat.SevenZip,
        _ => throw new ArchiveWorkerFailedException(),
    };

    private static ArchiveException MapFailure(string code) => code switch
    {
        "archive_unsupported" => new ArchiveUnsupportedException(),
        "archive_invalid" => new ArchiveInvalidException(),
        "archive_encrypted" => new ArchiveEncryptedException(),
        "archive_volume_set_invalid" => new ArchiveVolumeSetInvalidException([]),
        "archive_entry_unsafe" => new ArchiveEntryUnsafeException(),
        "archive_limit_exceeded" => new ArchiveLimitExceededException(
            "The archive exceeds a configured inspection limit."),
        _ => new ArchiveWorkerFailedException(),
    };

    private static async Task IgnoreMonitorCancellationAsync(Task monitorTask)
    {
        try
        {
            await monitorTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task IgnoreCaptureFailureAsync(Task<byte[]> captureTask)
    {
        try
        {
            await captureTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static async ValueTask TerminateAsync(IArchiveWorkerProcess process)
    {
        try
        {
            process.KillEntireProcessTree();
        }
        catch
        {
        }

        try
        {
            if (!process.HasExited)
            {
                using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
            }
        }
        catch
        {
        }
    }
}

using ReachCommander.ArchiveProtocol;

namespace ReachCommander.ArchiveWorker;

internal sealed class WorkerRequestDispatcher(SharpCompressArchiveAdapter adapter)
{
    public async Task DispatchAsync(
        Stream input,
        Stream output,
        CancellationToken cancellationToken)
    {
        var reader = new ArchiveFrameReader(input);
        var writer = new ArchiveFrameWriter(output);
        try
        {
            var frame = await reader.ReadRequestAsync(cancellationToken).ConfigureAwait(false);
            await reader.EnsureEndOfInputAsync(cancellationToken).ConfigureAwait(false);
            if (frame.Kind == ArchiveFrameKind.InspectionRequest)
            {
                await DispatchInspectionAsync(
                    frame.Deserialize<ArchiveInspectionRequest>(),
                    writer,
                    cancellationToken).ConfigureAwait(false);
            }
            else if (frame.Kind == ArchiveFrameKind.ExtractionRequest)
            {
                await DispatchExtractionAsync(
                    frame.Deserialize<ArchiveExtractionRequest>(),
                    writer,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                throw WorkerFailure.Protocol();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (WorkerFailure failure)
        {
            await writer.WriteJsonAsync(
                ArchiveFrameKind.Failure,
                failure.Frame,
                cancellationToken).ConfigureAwait(false);
        }
        catch (ArchiveProtocolException)
        {
            await writer.WriteJsonAsync(
                ArchiveFrameKind.Failure,
                WorkerFailure.Protocol().Frame,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await writer.WriteJsonAsync(
                ArchiveFrameKind.Failure,
                WorkerFailure.Unexpected().Frame,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DispatchInspectionAsync(
        ArchiveInspectionRequest request,
        ArchiveFrameWriter writer,
        CancellationToken cancellationToken)
    {
        var result = adapter.Inspect(request);
        await writer.WriteJsonAsync(
            ArchiveFrameKind.ArchiveDetected,
            new ArchiveDetectedFrame(result.Format, result.IsSolid),
            cancellationToken).ConfigureAwait(false);
        foreach (var entry in result.Entries)
        {
            await writer.WriteJsonAsync(
                ArchiveFrameKind.ArchiveEntry,
                entry,
                cancellationToken).ConfigureAwait(false);
        }

        await writer.WriteEmptyAsync(
            ArchiveFrameKind.InspectionCompleted,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task DispatchExtractionAsync(
        ArchiveExtractionRequest request,
        ArchiveFrameWriter writer,
        CancellationToken cancellationToken)
    {
        await adapter.ExtractAsync(
            request,
            new FramedExtractionSink(writer),
            cancellationToken).ConfigureAwait(false);
    }

    private sealed class FramedExtractionSink(ArchiveFrameWriter writer) : IWorkerArchiveEntrySink
    {
        public ValueTask StartAsync(int entryIndex, CancellationToken cancellationToken) =>
            writer.WriteJsonAsync(
                ArchiveFrameKind.EntryStart,
                new ArchiveEntryStartFrame(entryIndex),
                cancellationToken);

        public ValueTask WriteAsync(
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken) =>
            writer.WriteDataAsync(data, cancellationToken);

        public ValueTask EndAsync(
            int entryIndex,
            long actualBytes,
            CancellationToken cancellationToken) =>
            writer.WriteJsonAsync(
                ArchiveFrameKind.EntryEnd,
                new ArchiveEntryEndFrame(entryIndex, actualBytes),
                cancellationToken);

        public ValueTask ProgressAsync(
            int completedFiles,
            long actualBytes,
            CancellationToken cancellationToken) =>
            writer.WriteJsonAsync(
                ArchiveFrameKind.Progress,
                new ArchiveProgressFrame(completedFiles, actualBytes),
                cancellationToken);

        public ValueTask CompleteAsync(
            int completedFiles,
            long actualBytes,
            CancellationToken cancellationToken) =>
            writer.WriteJsonAsync(
                ArchiveFrameKind.Completed,
                new ArchiveCompletedFrame(completedFiles, actualBytes),
                cancellationToken);
    }
}

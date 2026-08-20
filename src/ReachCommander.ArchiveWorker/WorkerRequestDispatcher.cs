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
            if (frame.Kind != ArchiveFrameKind.InspectionRequest)
            {
                throw WorkerFailure.Protocol();
            }

            var request = frame.Deserialize<ArchiveInspectionRequest>();
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
}

using ReachCommander.ArchiveProtocol;

namespace ReachCommander.ArchiveWorker;

internal sealed class ArchiveFrameWriter(Stream output)
{
    public ValueTask WriteJsonAsync<T>(
        ArchiveFrameKind kind,
        T value,
        CancellationToken cancellationToken) =>
        ArchiveFrameCodec.WriteJsonAsync(output, kind, value, cancellationToken);

    public ValueTask WriteEmptyAsync(
        ArchiveFrameKind kind,
        CancellationToken cancellationToken) =>
        ArchiveFrameCodec.WriteAsync(output, kind, ReadOnlyMemory<byte>.Empty, cancellationToken);

    public ValueTask WriteDataAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken) =>
        ArchiveFrameCodec.WriteAsync(
            output,
            ArchiveFrameKind.EntryData,
            data,
            cancellationToken);
}

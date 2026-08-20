using ReachCommander.ArchiveProtocol;

namespace ReachCommander.ArchiveWorker;

internal sealed class ArchiveFrameReader(Stream input)
{
    public ValueTask<ArchiveFrame> ReadRequestAsync(CancellationToken cancellationToken) =>
        ArchiveFrameCodec.ReadAsync(
            input,
            ArchiveFrameCodec.MaxJsonPayloadBytes,
            cancellationToken);

    public async ValueTask EnsureEndOfInputAsync(CancellationToken cancellationToken)
    {
        var trailing = new byte[1];
        if (await input.ReadAsync(trailing, cancellationToken).ConfigureAwait(false) != 0)
        {
            throw new ArchiveProtocolException();
        }
    }
}

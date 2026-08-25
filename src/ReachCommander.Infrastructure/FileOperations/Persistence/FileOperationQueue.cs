using System.Threading.Channels;

namespace ReachCommander.Infrastructure.FileOperations.Persistence;

internal sealed class FileOperationQueue
{
    private readonly Channel<bool> _signals = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });

    internal void Signal() => _signals.Writer.TryWrite(true);

    internal async Task WaitAsync(CancellationToken cancellationToken) =>
        _ = await _signals.Reader.ReadAsync(cancellationToken);
}

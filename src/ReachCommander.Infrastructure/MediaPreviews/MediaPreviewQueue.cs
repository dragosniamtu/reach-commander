using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace ReachCommander.Infrastructure.MediaPreviews;

internal sealed class MediaPreviewQueue(IOptions<MediaPreviewOptions> options)
{
    private readonly Channel<Guid> _channel = Channel.CreateBounded<Guid>(
        new BoundedChannelOptions(options.Value.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

    public bool TryEnqueue(Guid sessionId) => _channel.Writer.TryWrite(sessionId);

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}

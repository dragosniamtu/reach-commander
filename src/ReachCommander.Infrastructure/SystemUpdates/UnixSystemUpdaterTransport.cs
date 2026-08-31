using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;

namespace ReachCommander.Infrastructure.SystemUpdates;

internal interface ISystemUpdaterTransport
{
    Task<string> ExchangeAsync(string request, CancellationToken cancellationToken);

    Task<string> ExchangeAsync(
        string request,
        int maximumResponseBytes,
        CancellationToken cancellationToken) => ExchangeAsync(request, cancellationToken);
}

internal sealed class UnixSystemUpdaterTransport(IOptions<SystemUpdateOptions> options)
    : ISystemUpdaterTransport
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public async Task<string> ExchangeAsync(string request, CancellationToken cancellationToken)
        => await ExchangeAsync(
            request,
            SystemUpdaterGateway.MaximumMessageBytes,
            cancellationToken).ConfigureAwait(false);

    public async Task<string> ExchangeAsync(
        string request,
        int maximumResponseBytes,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var requestBytes = Encoding.UTF8.GetBytes(request);
        if (requestBytes.Length > SystemUpdaterGateway.MaximumMessageBytes)
        {
            throw new SystemUpdaterProtocolException("The updater request is too large.");
        }

        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        var requestMayHaveBeenAccepted = false;
        try
        {
            using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connectTimeout.CancelAfter(settings.ConnectTimeout);
                await socket.ConnectAsync(
                    new UnixDomainSocketEndPoint(settings.SocketPath),
                    connectTimeout.Token).ConfigureAwait(false);
            }

            using var responseTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            responseTimeout.CancelAfter(settings.ResponseTimeout);
            requestMayHaveBeenAccepted = true;
            await SendAllAsync(socket, requestBytes, responseTimeout.Token).ConfigureAwait(false);
            return await ReceiveLineAsync(
                socket,
                maximumResponseBytes,
                responseTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SystemUpdaterUnavailableException(
                "The host updater did not respond in time.",
                requestMayHaveBeenAccepted);
        }
        catch (SocketException)
        {
            throw new SystemUpdaterUnavailableException(
                "The host updater socket is unavailable.",
                requestMayHaveBeenAccepted);
        }
        catch (UnauthorizedAccessException)
        {
            throw new SystemUpdaterUnavailableException("The host updater socket is unavailable.");
        }
        catch (IOException)
        {
            throw new SystemUpdaterUnavailableException(
                "The host updater connection ended unexpectedly.",
                requestMayHaveBeenAccepted);
        }
    }

    private static async Task SendAllAsync(
        Socket socket,
        ReadOnlyMemory<byte> message,
        CancellationToken cancellationToken)
    {
        var sent = 0;
        while (sent < message.Length)
        {
            var count = await socket.SendAsync(message[sent..], SocketFlags.None, cancellationToken)
                .ConfigureAwait(false);
            if (count == 0)
            {
                throw new IOException("The updater connection closed while sending.");
            }

            sent += count;
        }
    }

    private static async Task<string> ReceiveLineAsync(
        Socket socket,
        int maximumResponseBytes,
        CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(4096);
        var buffer = new byte[4096];
        var terminated = false;
        while (true)
        {
            var count = await socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken)
                .ConfigureAwait(false);
            if (count == 0)
            {
                if (!terminated)
                {
                    throw new IOException("The updater response was not newline terminated.");
                }

                try
                {
                    return StrictUtf8.GetString(bytes.ToArray());
                }
                catch (DecoderFallbackException)
                {
                    throw new SystemUpdaterProtocolException(
                        "The updater response is not valid UTF-8.");
                }
            }

            for (var index = 0; index < count; index++)
            {
                if (terminated || buffer[index] == (byte)'\n')
                {
                    if (terminated || index != count - 1)
                    {
                        throw new SystemUpdaterProtocolException(
                            "The updater response contains more than one frame.");
                    }

                    terminated = true;
                    continue;
                }

                bytes.Add(buffer[index]);
                if (bytes.Count > maximumResponseBytes)
                {
                    throw new SystemUpdaterProtocolException("The updater response is too large.");
                }
            }
        }
    }
}

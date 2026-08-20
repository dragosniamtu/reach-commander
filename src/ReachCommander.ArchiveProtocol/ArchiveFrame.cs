using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace ReachCommander.ArchiveProtocol;

public enum ArchiveFrameKind : byte
{
    InspectionRequest = 1,
    ExtractionRequest = 2,
    ArchiveDetected = 3,
    ArchiveEntry = 4,
    InspectionCompleted = 5,
    EntryStart = 6,
    EntryData = 7,
    EntryEnd = 8,
    Progress = 9,
    Completed = 10,
    Failure = 11,
}

public sealed class ArchiveProtocolException : Exception
{
    public ArchiveProtocolException()
        : base("The archive worker protocol is invalid.")
    {
    }

    public ArchiveProtocolException(Exception innerException)
        : base("The archive worker protocol is invalid.", innerException)
    {
    }
}

public readonly record struct ArchiveFrame(
    byte ProtocolVersion,
    ArchiveFrameKind Kind,
    ReadOnlyMemory<byte> Payload)
{
    public T Deserialize<T>()
    {
        if (Kind == ArchiveFrameKind.EntryData)
        {
            throw new ArchiveProtocolException();
        }

        var typeInfo = ArchiveProtocolJsonContext.Default.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
            ?? throw new ArchiveProtocolException();
        try
        {
            return JsonSerializer.Deserialize(Payload.Span, typeInfo)
                ?? throw new ArchiveProtocolException();
        }
        catch (JsonException exception)
        {
            throw new ArchiveProtocolException(exception);
        }
    }
}

public static class ArchiveFrameCodec
{
    public const byte CurrentProtocolVersion = 1;
    public const int HeaderLength = 10;
    public const int MaxJsonPayloadBytes = 1024 * 1024;
    public const int MaxDataPayloadBytes = 64 * 1024;

    private static ReadOnlySpan<byte> Magic => "RCAR"u8;

    public static async ValueTask<ArchiveFrame> ReadAsync(
        Stream stream,
        int maximumPayloadBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (maximumPayloadBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));
        }

        var header = new byte[HeaderLength];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new ArchiveProtocolException();
        }

        var version = header[4];
        if (version != CurrentProtocolVersion)
        {
            throw new ArchiveProtocolException();
        }

        var kind = (ArchiveFrameKind)header[5];
        ValidateKind(kind);
        var payloadLength = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(6, 4));
        var protocolLimit = GetPayloadLimit(kind);
        if (payloadLength > maximumPayloadBytes || payloadLength > protocolLimit)
        {
            throw new ArchiveProtocolException();
        }

        var payload = new byte[(int)payloadLength];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        return new ArchiveFrame(version, kind, payload);
    }

    public static async ValueTask WriteJsonAsync<T>(
        Stream stream,
        ArchiveFrameKind kind,
        T value,
        CancellationToken cancellationToken)
    {
        if (kind == ArchiveFrameKind.EntryData)
        {
            throw new ArchiveProtocolException();
        }

        var typeInfo = ArchiveProtocolJsonContext.Default.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
            ?? throw new ArchiveProtocolException();
        byte[] payload;
        try
        {
            payload = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        }
        catch (JsonException exception)
        {
            throw new ArchiveProtocolException(exception);
        }

        await WriteAsync(stream, kind, payload, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask WriteAsync(
        Stream stream,
        ArchiveFrameKind kind,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ValidateKind(kind);
        if (payload.Length > GetPayloadLimit(kind))
        {
            throw new ArchiveProtocolException();
        }

        var header = new byte[HeaderLength];
        Magic.CopyTo(header);
        header[4] = CurrentProtocolVersion;
        header[5] = (byte)kind;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(6, 4), (uint)payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static int GetPayloadLimit(ArchiveFrameKind kind) =>
        kind == ArchiveFrameKind.EntryData
            ? MaxDataPayloadBytes
            : MaxJsonPayloadBytes;

    private static void ValidateKind(ArchiveFrameKind kind)
    {
        if (kind is < ArchiveFrameKind.InspectionRequest or > ArchiveFrameKind.Failure)
        {
            throw new ArchiveProtocolException();
        }
    }

    private static async ValueTask ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        try
        {
            await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException exception)
        {
            throw new ArchiveProtocolException(exception);
        }
    }
}

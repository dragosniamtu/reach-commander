using ReachCommander.ArchiveProtocol;

namespace ReachCommander.UnitTests.Archives;

public sealed class ArchiveWorkerProtocolTests
{
    [Fact]
    public async Task Round_trips_a_versioned_inspection_request()
    {
        var request = new ArchiveInspectionRequest(
            ProtocolVersion: 1,
            RequestId: "request-1",
            VolumePaths: ["/srv/archive.7z.001", "/srv/archive.7z.002"],
            Limits: new ArchiveWorkerLimits(100_000, 500L * 1024 * 1024 * 1024));
        await using var stream = new MemoryStream();

        await ArchiveFrameCodec.WriteJsonAsync(
            stream,
            ArchiveFrameKind.InspectionRequest,
            request,
            default);
        stream.Position = 0;
        var frame = await ArchiveFrameCodec.ReadAsync(stream, 1_048_576, default);
        var actual = frame.Deserialize<ArchiveInspectionRequest>();

        Assert.Equal(ArchiveFrameKind.InspectionRequest, frame.Kind);
        Assert.Equal(request, actual);
    }

    [Fact]
    public async Task Round_trips_opaque_entry_data()
    {
        var payload = new byte[] { 0, 1, 2, 127, 128, 255 };
        await using var stream = new MemoryStream();

        await ArchiveFrameCodec.WriteAsync(
            stream,
            ArchiveFrameKind.EntryData,
            payload,
            default);
        stream.Position = 0;
        var frame = await ArchiveFrameCodec.ReadAsync(stream, 64 * 1024, default);

        Assert.Equal(ArchiveFrameKind.EntryData, frame.Kind);
        Assert.Equal(payload, frame.Payload.ToArray());
    }

    [Fact]
    public async Task Rejects_a_frame_above_the_reader_limit()
    {
        await using var stream = new MemoryStream(
            [82, 67, 65, 82, 1, 1, 0, 0, 0, 16]);

        await Assert.ThrowsAsync<ArchiveProtocolException>(
            () => ArchiveFrameCodec.ReadAsync(stream, 8, default).AsTask());
    }

    [Theory]
    [MemberData(nameof(InvalidFrames))]
    public async Task Rejects_malformed_frames(byte[] bytes)
    {
        await using var stream = new MemoryStream(bytes);

        await Assert.ThrowsAsync<ArchiveProtocolException>(
            () => ArchiveFrameCodec.ReadAsync(stream, 1_048_576, default).AsTask());
    }

    public static TheoryData<byte[]> InvalidFrames => new()
    {
        Array.Empty<byte>(),
        new byte[] { 82, 67, 65 },
        new byte[] { 66, 65, 68, 33, 1, 1, 0, 0, 0, 0 },
        new byte[] { 82, 67, 65, 82, 2, 1, 0, 0, 0, 0 },
        new byte[] { 82, 67, 65, 82, 1, 255, 0, 0, 0, 0 },
        new byte[] { 82, 67, 65, 82, 1, 1, 0, 0, 0, 1 },
    };

    [Fact]
    public async Task Rejects_json_written_for_entry_data()
    {
        await using var stream = new MemoryStream();

        await Assert.ThrowsAsync<ArchiveProtocolException>(() =>
            ArchiveFrameCodec.WriteJsonAsync(
                stream,
                ArchiveFrameKind.EntryData,
                new ArchiveEntryStartFrame(1),
                default).AsTask());
    }

    [Fact]
    public async Task Enforces_protocol_payload_caps_when_writing()
    {
        await using var metadataStream = new MemoryStream();
        await using var dataStream = new MemoryStream();

        await Assert.ThrowsAsync<ArchiveProtocolException>(() =>
            ArchiveFrameCodec.WriteAsync(
                metadataStream,
                ArchiveFrameKind.ArchiveEntry,
                new byte[(1024 * 1024) + 1],
                default).AsTask());
        await Assert.ThrowsAsync<ArchiveProtocolException>(() =>
            ArchiveFrameCodec.WriteAsync(
                dataStream,
                ArchiveFrameKind.EntryData,
                new byte[(64 * 1024) + 1],
                default).AsTask());
    }
}

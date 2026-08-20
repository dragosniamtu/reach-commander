using ReachCommander.ArchiveProtocol;
using ReachCommander.ArchiveWorker;
using System.Text;

namespace ReachCommander.UnitTests.Archives;

public sealed class ArchiveWorkerInspectionTests
{
    private static readonly string FixtureRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "..",
        "fixtures",
        "archives"));

    [Theory]
    [InlineData("Zip.deflate.zip", "zip", false)]
    [InlineData("7Zip.nonsolid.7z", "sevenZip", false)]
    [InlineData("Rar.rar", "rar", false)]
    [InlineData("Rar.solid.rar", "rar", true)]
    public async Task Inspects_supported_single_volume_archives(
        string fixture,
        string expectedFormat,
        bool expectedSolid)
    {
        var frames = await InspectAsync([Fixture(fixture)]);

        AssertSuccessfulInspection(frames, expectedFormat, expectedSolid);
    }

    [Theory]
    [MemberData(nameof(ValidVolumeSets))]
    public async Task Inspects_complete_ordered_volume_sets(
        string expectedFormat,
        string[] fixtureNames)
    {
        var frames = await InspectAsync(fixtureNames.Select(Fixture).ToArray());

        AssertSuccessfulInspection(frames, expectedFormat, expectedSolid: null);
    }

    public static TheoryData<string, string[]> ValidVolumeSets => new()
    {
        {
            "rar",
            [
                "Rar.multi.part01.rar",
                "Rar.multi.part02.rar",
                "Rar.multi.part03.rar",
                "Rar.multi.part04.rar",
                "Rar.multi.part05.rar",
                "Rar.multi.part06.rar",
            ]
        },
        {
            "rar",
            [
                "Rar2.multi.rar",
                "Rar2.multi.r00",
                "Rar2.multi.r01",
                "Rar2.multi.r02",
                "Rar2.multi.r03",
                "Rar2.multi.r04",
                "Rar2.multi.r05",
            ]
        },
        {
            "sevenZip",
            [
                "Original.7z.001",
                "Original.7z.002",
                "Original.7z.003",
                "Original.7z.004",
                "Original.7z.005",
                "Original.7z.006",
                "Original.7z.007",
            ]
        },
        {
            "zip",
            ["Infozip.nocomp.multi.z01", "Infozip.nocomp.multi.zip"]
        },
    };

    [Theory]
    [InlineData("Rar.encrypted_filesOnly.rar")]
    [InlineData("7Zip.encryptedFiles.7z")]
    [InlineData("Zip.none.encrypted.zip")]
    public async Task Rejects_encrypted_archives(string fixture)
    {
        var frames = await InspectAsync([Fixture(fixture)]);

        var failure = AssertSingleFailure(frames);
        Assert.Equal("archive_encrypted", failure.Code);
        Assert.DoesNotContain(FixtureRoot, failure.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rejects_malformed_archives_without_exposing_parser_details()
    {
        var path = Fixture("Rar.malformed_512byte.rar");

        var frames = await InspectAsync([path]);

        var failure = AssertSingleFailure(frames);
        Assert.Equal("archive_invalid", failure.Code);
        Assert.DoesNotContain(path, failure.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rejects_unsupported_archive_signatures()
    {
        var frames = await InspectAsync([Fixture("7Zip.Tar.tar")]);

        Assert.Equal("archive_unsupported", AssertSingleFailure(frames).Code);
    }

    [Fact]
    public async Task Rejects_entry_count_breaches()
    {
        var frames = await InspectAsync(
            [Fixture("Zip.deflate.zip")],
            new ArchiveWorkerLimits(1, long.MaxValue));

        Assert.Equal("archive_limit_exceeded", AssertSingleFailure(frames).Code);
    }

    [Fact]
    public async Task Rejects_mixed_bytes_in_a_same_named_volume_set()
    {
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"reachcommander-worker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var names = new[]
            {
                "Rar.multi.part01.rar",
                "Rar.multi.part02.rar",
                "Rar.multi.part03.rar",
                "Rar.multi.part04.rar",
                "Rar.multi.part05.rar",
                "Rar.multi.part06.rar",
            };
            foreach (var name in names)
            {
                File.Copy(Fixture(name), Path.Combine(tempRoot, name));
            }

            File.Copy(
                Fixture("Rar.multi.solid.part02.rar"),
                Path.Combine(tempRoot, "Rar.multi.part02.rar"),
                overwrite: true);

            var frames = await InspectAsync(
                names.Select(name => Path.Combine(tempRoot, name)).ToArray());

            Assert.Equal("archive_volume_set_invalid", AssertSingleFailure(frames).Code);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Rejects_a_second_request_frame()
    {
        var request = CreateRequest([Fixture("Zip.deflate.zip")], DefaultLimits());
        await using var input = new MemoryStream();
        await ArchiveFrameCodec.WriteJsonAsync(
            input,
            ArchiveFrameKind.InspectionRequest,
            request,
            default);
        await ArchiveFrameCodec.WriteJsonAsync(
            input,
            ArchiveFrameKind.InspectionRequest,
            request,
            default);
        input.Position = 0;
        await using var output = new MemoryStream();

        await CreateDispatcher().DispatchAsync(input, output, default);

        var frames = await ReadFramesAsync(output);
        Assert.Equal("archive_invalid", AssertSingleFailure(frames).Code);
    }

    [Fact]
    public async Task Rejects_null_request_members_as_an_invalid_request()
    {
        await using var input = new MemoryStream();
        await ArchiveFrameCodec.WriteAsync(
            input,
            ArchiveFrameKind.InspectionRequest,
            Encoding.UTF8.GetBytes(
                "{\"protocolVersion\":1,\"requestId\":\"test\",\"volumePaths\":null,\"limits\":null}"),
            default);
        input.Position = 0;
        await using var output = new MemoryStream();

        await CreateDispatcher().DispatchAsync(input, output, default);

        var frames = await ReadFramesAsync(output);
        Assert.Equal("archive_invalid", AssertSingleFailure(frames).Code);
    }

    private static async Task<IReadOnlyList<ArchiveFrame>> InspectAsync(
        IReadOnlyList<string> paths,
        ArchiveWorkerLimits? limits = null)
    {
        await using var input = new MemoryStream();
        await ArchiveFrameCodec.WriteJsonAsync(
            input,
            ArchiveFrameKind.InspectionRequest,
            CreateRequest(paths, limits ?? DefaultLimits()),
            default);
        input.Position = 0;
        await using var output = new MemoryStream();

        await CreateDispatcher().DispatchAsync(input, output, default);

        return await ReadFramesAsync(output);
    }

    private static WorkerRequestDispatcher CreateDispatcher() =>
        new(new SharpCompressArchiveAdapter());

    private static ArchiveInspectionRequest CreateRequest(
        IReadOnlyList<string> paths,
        ArchiveWorkerLimits limits) =>
        new(
            ArchiveFrameCodec.CurrentProtocolVersion,
            "inspection-test",
            paths,
            limits);

    private static ArchiveWorkerLimits DefaultLimits() =>
        new(100_000, 500L * 1024 * 1024 * 1024);

    private static async Task<IReadOnlyList<ArchiveFrame>> ReadFramesAsync(MemoryStream output)
    {
        output.Position = 0;
        var frames = new List<ArchiveFrame>();
        while (output.Position < output.Length)
        {
            frames.Add(await ArchiveFrameCodec.ReadAsync(
                output,
                ArchiveFrameCodec.MaxJsonPayloadBytes,
                default));
        }

        return frames;
    }

    private static void AssertSuccessfulInspection(
        IReadOnlyList<ArchiveFrame> frames,
        string expectedFormat,
        bool? expectedSolid)
    {
        if (frames.Count == 1 && frames[0].Kind == ArchiveFrameKind.Failure)
        {
            var failure = frames[0].Deserialize<ArchiveFailureFrame>();
            Assert.Fail($"Inspection failed with {failure.Code}: {failure.Detail}");
        }

        Assert.Equal(ArchiveFrameKind.ArchiveDetected, frames[0].Kind);
        var detected = frames[0].Deserialize<ArchiveDetectedFrame>();
        Assert.Equal(expectedFormat, detected.Format);
        if (expectedSolid is not null)
        {
            Assert.Equal(expectedSolid, detected.IsSolid);
        }

        Assert.Contains(frames, frame => frame.Kind == ArchiveFrameKind.ArchiveEntry);
        Assert.Equal(ArchiveFrameKind.InspectionCompleted, frames[^1].Kind);
        Assert.DoesNotContain(frames, frame => frame.Kind == ArchiveFrameKind.Failure);
    }

    private static ArchiveFailureFrame AssertSingleFailure(IReadOnlyList<ArchiveFrame> frames)
    {
        var frame = Assert.Single(frames);
        Assert.Equal(ArchiveFrameKind.Failure, frame.Kind);
        return frame.Deserialize<ArchiveFailureFrame>();
    }

    private static string Fixture(string name) => Path.Combine(FixtureRoot, name);
}

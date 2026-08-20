using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ReachCommander.ArchiveProtocol;
using ReachCommander.ArchiveWorker;
using ReachCommander.UnitTests.Support;

namespace ReachCommander.UnitTests.Archives;

public sealed class ArchiveWorkerExtractionTests
{
    private static readonly string FixtureRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "..",
        "fixtures",
        "archives"));

    [Fact]
    public async Task Emits_only_selected_files_with_bounded_ordered_frames_and_exact_totals()
    {
        using var temporary = new TemporaryDirectory();
        var archive = CreateZip(
            temporary.Path,
            ("folder/", null),
            ("folder/one.txt", "one"),
            ("two.txt", "second"));

        var frames = await ExtractAsync(archive, [1, 2]);

        Assert.Equal(
            [
                ArchiveFrameKind.EntryStart,
                ArchiveFrameKind.EntryData,
                ArchiveFrameKind.EntryEnd,
                ArchiveFrameKind.Progress,
                ArchiveFrameKind.EntryStart,
                ArchiveFrameKind.EntryData,
                ArchiveFrameKind.EntryEnd,
                ArchiveFrameKind.Progress,
                ArchiveFrameKind.Completed,
            ],
            frames.Select(frame => frame.Kind));
        Assert.Equal([1, 2], frames
            .Where(frame => frame.Kind == ArchiveFrameKind.EntryStart)
            .Select(frame => frame.Deserialize<ArchiveEntryStartFrame>().Index));
        Assert.Equal("onesecond", Encoding.UTF8.GetString(frames
            .Where(frame => frame.Kind == ArchiveFrameKind.EntryData)
            .SelectMany(frame => frame.Payload.ToArray())
            .ToArray()));
        Assert.Equal(3, frames[2].Deserialize<ArchiveEntryEndFrame>().ActualBytes);
        Assert.Equal(6, frames[6].Deserialize<ArchiveEntryEndFrame>().ActualBytes);
        var completed = frames[^1].Deserialize<ArchiveCompletedFrame>();
        Assert.Equal(2, completed.CompletedFiles);
        Assert.Equal(9, completed.ActualBytes);
    }

    [Fact]
    public async Task Rejects_duplicate_directory_or_missing_indexes_before_emitting_entry_bytes()
    {
        using var temporary = new TemporaryDirectory();
        var archive = CreateZip(
            temporary.Path,
            ("folder/", null),
            ("folder/one.txt", "one"));

        foreach (var indexes in new[] { new[] { 1, 1 }, new[] { 0 }, new[] { 99 } })
        {
            var frames = await ExtractAsync(archive, indexes);

            Assert.Equal("archive_invalid", AssertSingleFailure(frames).Code);
            Assert.DoesNotContain(frames, frame => frame.Kind is
                ArchiveFrameKind.EntryStart or ArchiveFrameKind.EntryData or ArchiveFrameKind.Completed);
        }
    }

    [Fact]
    public async Task Chunks_payloads_at_64_kib_and_reports_actual_streamed_bytes()
    {
        using var temporary = new TemporaryDirectory();
        var content = new string('x', ArchiveFrameCodec.MaxDataPayloadBytes + 123);
        var archive = CreateZip(temporary.Path, ("large.txt", content));

        var frames = await ExtractAsync(archive, [0]);
        var data = frames.Where(frame => frame.Kind == ArchiveFrameKind.EntryData).ToArray();

        Assert.Equal(2, data.Length);
        Assert.All(data, frame => Assert.InRange(
            frame.Payload.Length,
            1,
            ArchiveFrameCodec.MaxDataPayloadBytes));
        Assert.Equal(content.Length, data.Sum(frame => frame.Payload.Length));
        Assert.Equal(content.Length, frames
            .Single(frame => frame.Kind == ArchiveFrameKind.EntryEnd)
            .Deserialize<ArchiveEntryEndFrame>().ActualBytes);
        Assert.Equal(content.Length, frames
            .Single(frame => frame.Kind == ArchiveFrameKind.Progress)
            .Deserialize<ArchiveProgressFrame>().ActualBytes);
    }

    [Fact]
    public async Task Rejects_encrypted_entries_before_emitting_any_entry_bytes()
    {
        var path = Path.Combine(FixtureRoot, "Zip.none.encrypted.zip");

        var frames = await ExtractAsync(path, [0]);

        Assert.Equal("archive_encrypted", AssertSingleFailure(frames).Code);
        Assert.DoesNotContain(frames, frame => frame.Kind is
            ArchiveFrameKind.EntryStart or ArchiveFrameKind.EntryData or ArchiveFrameKind.Completed);
    }

    [Fact]
    public async Task Solid_archive_selection_is_emitted_once_in_natural_entry_order()
    {
        var path = Path.Combine(FixtureRoot, "Rar.solid.rar");
        var inspected = await InspectAsync(path);
        var fileIndexes = inspected
            .Where(entry => !entry.IsDirectory && !entry.IsEncrypted && !entry.IsLink && !entry.IsSpecial)
            .Select(entry => entry.Index)
            .Take(2)
            .Reverse()
            .ToArray();
        Assert.Equal(2, fileIndexes.Length);

        var frames = await ExtractAsync(path, fileIndexes);
        var emitted = frames
            .Where(frame => frame.Kind == ArchiveFrameKind.EntryStart)
            .Select(frame => frame.Deserialize<ArchiveEntryStartFrame>().Index)
            .ToArray();

        Assert.Equal(fileIndexes.Order(), emitted);
        Assert.Equal(emitted.Length, emitted.Distinct().Count());
        Assert.Equal(ArchiveFrameKind.Completed, frames[^1].Kind);
    }

    [Fact]
    public async Task Numbered_split_zip_reproduces_the_original_catalog_and_content_checksum()
    {
        var originalPath = Path.Combine(FixtureRoot, "nested.zip");
        var splitPaths = new[]
        {
            Path.Combine(FixtureRoot, "split.zip.001"),
            Path.Combine(FixtureRoot, "split.zip.002"),
            Path.Combine(FixtureRoot, "split.zip.003"),
        };
        var originalCatalog = await InspectAsync([originalPath]);
        var splitCatalog = await InspectAsync(splitPaths);

        Assert.NotEmpty(splitCatalog);
        Assert.Equal(
            originalCatalog.Select(entry => (entry.Key, entry.Size)),
            splitCatalog.Select(entry => (entry.Key, entry.Size)));
        var indexes = splitCatalog
            .Where(entry => !entry.IsDirectory)
            .Select(entry => entry.Index)
            .ToArray();
        var original = await ExtractAsync([originalPath], indexes);
        var split = await ExtractAsync(splitPaths, indexes);

        Assert.Equal(PayloadChecksum(original), PayloadChecksum(split));
    }

    private static async Task<IReadOnlyList<ArchiveFrame>> ExtractAsync(
        string archive,
        IReadOnlyList<int> entryIndexes) => await ExtractAsync([archive], entryIndexes);

    private static async Task<IReadOnlyList<ArchiveFrame>> ExtractAsync(
        IReadOnlyList<string> archives,
        IReadOnlyList<int> entryIndexes)
    {
        await using var input = new MemoryStream();
        await ArchiveFrameCodec.WriteJsonAsync(
            input,
            ArchiveFrameKind.ExtractionRequest,
            new ArchiveExtractionRequest(
                ArchiveFrameCodec.CurrentProtocolVersion,
                "extraction-test",
                archives,
                entryIndexes,
                Limits()),
            default);
        input.Position = 0;
        await using var output = new MemoryStream();

        await new WorkerRequestDispatcher(new SharpCompressArchiveAdapter())
            .DispatchAsync(input, output, default);

        return await ReadFramesAsync(output);
    }

    private static async Task<IReadOnlyList<ArchiveEntryFrame>> InspectAsync(string archive) =>
        await InspectAsync([archive]);

    private static async Task<IReadOnlyList<ArchiveEntryFrame>> InspectAsync(
        IReadOnlyList<string> archives)
    {
        await using var input = new MemoryStream();
        await ArchiveFrameCodec.WriteJsonAsync(
            input,
            ArchiveFrameKind.InspectionRequest,
            new ArchiveInspectionRequest(
                ArchiveFrameCodec.CurrentProtocolVersion,
                "inspection-test",
                archives,
                Limits()),
            default);
        input.Position = 0;
        await using var output = new MemoryStream();
        await new WorkerRequestDispatcher(new SharpCompressArchiveAdapter())
            .DispatchAsync(input, output, default);
        var frames = await ReadFramesAsync(output);
        return frames
            .Where(frame => frame.Kind == ArchiveFrameKind.ArchiveEntry)
            .Select(frame => frame.Deserialize<ArchiveEntryFrame>())
            .ToArray();
    }

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

    private static ArchiveFailureFrame AssertSingleFailure(IReadOnlyList<ArchiveFrame> frames)
    {
        var frame = Assert.Single(frames);
        Assert.Equal(ArchiveFrameKind.Failure, frame.Kind);
        return frame.Deserialize<ArchiveFailureFrame>();
    }

    private static ArchiveWorkerLimits Limits() =>
        new(100_000, 500L * 1024 * 1024 * 1024);

    private static string PayloadChecksum(IReadOnlyList<ArchiveFrame> frames) =>
        Convert.ToHexString(SHA256.HashData(frames
            .Where(frame => frame.Kind == ArchiveFrameKind.EntryData)
            .SelectMany(frame => frame.Payload.ToArray())
            .ToArray()));

    private static string CreateZip(
        string directory,
        params (string Name, string? Content)[] entries)
    {
        var path = Path.Combine(directory, "sample.zip");
        using var file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
            if (content is null)
            {
                continue;
            }

            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }

        return path;
    }
}

using System.Diagnostics;
using Microsoft.Extensions.Options;
using ReachCommander.Application.Archives;
using ReachCommander.ArchiveProtocol;
using ReachCommander.Domain.Archives;
using ReachCommander.Infrastructure.Archives;
using ReachCommander.Infrastructure.Archives.Volumes;
using ReachCommander.Infrastructure.Archives.Worker;

namespace ReachCommander.UnitTests.Archives;

public sealed class ArchiveWorkerClientTests
{
    [Fact]
    public async Task Maps_process_start_failure_to_a_safe_worker_error()
    {
        var client = CreateClient(new ThrowingProcessFactory());

        var exception = await Assert.ThrowsAsync<ArchiveWorkerFailedException>(() =>
            client.InspectAsync(PartSet(), default).AsTask());

        Assert.Equal("The isolated archive worker failed.", exception.Detail);
    }

    [Fact]
    public async Task Starts_dotnet_safely_and_sends_physical_paths_only_in_stdin()
    {
        var output = await SuccessfulOutputAsync();
        var process = new FakeProcess(output) { ExitCodeValue = 0 };
        var factory = new FakeProcessFactory(process);
        var client = CreateClient(factory);
        var parts = PartSet();

        var result = await client.InspectAsync(parts, default);

        Assert.Equal(ArchiveFormat.Zip, result.Format);
        Assert.Single(result.Entries);
        var start = Assert.IsType<ProcessStartInfo>(factory.StartInfo);
        Assert.Equal("dotnet", start.FileName);
        Assert.False(start.UseShellExecute);
        Assert.True(start.RedirectStandardInput);
        Assert.True(start.RedirectStandardOutput);
        Assert.True(start.RedirectStandardError);
        Assert.Single(start.ArgumentList);
        Assert.EndsWith(
            Path.Combine("archive-worker", "ReachCommander.ArchiveWorker.dll"),
            start.ArgumentList[0],
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(parts.Parts[0].PhysicalPath, start.ArgumentList);
        Assert.Equal("40000000", start.Environment["DOTNET_GCHeapHardLimit"]);

        process.StandardInput.Position = 0;
        var requestFrame = await ArchiveFrameCodec.ReadAsync(
            process.StandardInput,
            ArchiveFrameCodec.MaxJsonPayloadBytes,
            default);
        var request = requestFrame.Deserialize<ArchiveInspectionRequest>();
        Assert.Equal(
            parts.Parts.Select(part => part.PhysicalPath),
            request.VolumePaths);
        Assert.DoesNotContain(parts.PrimaryLogicalPath, process.ReadInputAsText());
    }

    [Fact]
    public async Task Kills_the_process_tree_on_an_invalid_frame()
    {
        var process = new FakeProcess(new MemoryStream([1, 2, 3]));
        var client = CreateClient(new FakeProcessFactory(process));

        await Assert.ThrowsAsync<ArchiveWorkerFailedException>(() =>
            client.InspectAsync(PartSet(), default).AsTask());

        Assert.True(process.Killed);
    }

    [Fact]
    public async Task Kills_the_process_tree_on_cancellation()
    {
        var process = new FakeProcess(new BlockingReadStream());
        var client = CreateClient(new FakeProcessFactory(process));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.InspectAsync(PartSet(), cancellation.Token).AsTask());

        Assert.True(process.Killed);
    }

    [Fact]
    public async Task Kills_the_process_tree_when_working_set_exceeds_the_limit()
    {
        var process = new FakeProcess(new BlockingReadStream())
        {
            WorkingSetBytesValue = 1_536L * 1024 * 1024 + 1,
        };
        var workerDelay = new ImmediateWorkerDelay();
        var client = CreateClient(new FakeProcessFactory(process), workerDelay);

        var exception = await Assert.ThrowsAsync<ArchiveLimitExceededException>(() =>
            client.InspectAsync(PartSet(), default).AsTask());

        Assert.Equal("archive_limit_exceeded", exception.Code);
        Assert.True(process.Killed);
        Assert.True(process.WorkingSetSamples > 0);
        Assert.All(workerDelay.RequestedDelays, delay =>
            Assert.Equal(TimeSpan.FromMilliseconds(250), delay));
    }

    [Fact]
    public async Task Kills_the_process_tree_on_the_inspection_deadline()
    {
        var process = new FakeProcess(new BlockingReadStream());
        var client = CreateClient(
            new FakeProcessFactory(process),
            new ArchiveWorkerDelay(),
            new ArchiveOptions { InspectionTimeout = TimeSpan.FromMilliseconds(20) });

        var exception = await Assert.ThrowsAsync<ArchiveLimitExceededException>(() =>
            client.InspectAsync(PartSet(), default).AsTask());

        Assert.Contains("time limit", exception.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.True(process.Killed);
    }

    [Fact]
    public async Task Rejects_an_unexpected_nonzero_exit()
    {
        var process = new FakeProcess(await SuccessfulOutputAsync())
        {
            ExitCodeValue = 7,
        };
        var client = CreateClient(new FakeProcessFactory(process));

        await Assert.ThrowsAsync<ArchiveWorkerFailedException>(() =>
            client.InspectAsync(PartSet(), default).AsTask());

        Assert.True(process.Killed);
    }

    [Fact]
    public async Task Drains_stderr_but_never_returns_its_contents()
    {
        var secret = $"parser:{PartSet().Parts[0].PhysicalPath}";
        var stderr = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            string.Concat(Enumerable.Repeat(secret, 2048))));
        await using var output = new MemoryStream();
        await ArchiveFrameCodec.WriteJsonAsync(
            output,
            ArchiveFrameKind.Failure,
            new ArchiveFailureFrame("archive_worker_failed", "unsafe parser detail"),
            default);
        output.Position = 0;
        var process = new FakeProcess(output, stderr);
        var client = CreateClient(new FakeProcessFactory(process));

        var exception = await Assert.ThrowsAsync<ArchiveWorkerFailedException>(() =>
            client.InspectAsync(PartSet(), default).AsTask());

        Assert.Equal("The isolated archive worker failed.", exception.Detail);
        Assert.DoesNotContain(secret, exception.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(stderr.Length, stderr.Position);
    }

    [Fact]
    public async Task Rejects_out_of_order_or_duplicate_entry_indexes()
    {
        await using var output = new MemoryStream();
        await ArchiveFrameCodec.WriteJsonAsync(
            output,
            ArchiveFrameKind.ArchiveDetected,
            new ArchiveDetectedFrame("zip", false),
            default);
        await ArchiveFrameCodec.WriteJsonAsync(
            output,
            ArchiveFrameKind.ArchiveEntry,
            Entry(index: 1),
            default);
        await ArchiveFrameCodec.WriteAsync(
            output,
            ArchiveFrameKind.InspectionCompleted,
            ReadOnlyMemory<byte>.Empty,
            default);
        output.Position = 0;
        var process = new FakeProcess(output);
        var client = CreateClient(new FakeProcessFactory(process));

        await Assert.ThrowsAsync<ArchiveWorkerFailedException>(() =>
            client.InspectAsync(PartSet(), default).AsTask());

        Assert.True(process.Killed);
    }

    [Fact]
    public async Task Maps_worker_failure_to_a_safe_application_exception()
    {
        await using var output = new MemoryStream();
        await ArchiveFrameCodec.WriteJsonAsync(
            output,
            ArchiveFrameKind.Failure,
            new ArchiveFailureFrame("archive_encrypted", "Encrypted archives are not supported."),
            default);
        output.Position = 0;
        var process = new FakeProcess(output);
        var client = CreateClient(new FakeProcessFactory(process));

        var exception = await Assert.ThrowsAsync<ArchiveEncryptedException>(() =>
            client.InspectAsync(PartSet(), default).AsTask());

        Assert.Equal("archive_encrypted", exception.Code);
        Assert.DoesNotContain(
            PartSet().Parts[0].PhysicalPath,
            exception.Detail,
            StringComparison.OrdinalIgnoreCase);
    }

    private static ArchiveWorkerClient CreateClient(
        IArchiveWorkerProcessFactory processFactory,
        IArchiveWorkerDelay? delay = null,
        ArchiveOptions? options = null) =>
        new(
            processFactory,
            delay ?? new ImmediateWorkerDelay(),
            Options.Create(options ?? new ArchiveOptions()));

    private static ResolvedArchivePartSet PartSet() => new(
        ArchiveFormat.Zip,
        "/sample.zip",
        [new ResolvedArchivePart(
            "/sample.zip",
            Path.GetFullPath("sample.zip"),
            10,
            DateTimeOffset.Parse("2026-08-20T08:00:00Z"))],
        new ArchiveVolumeFingerprint("fingerprint"));

    private static async Task<MemoryStream> SuccessfulOutputAsync()
    {
        var output = new MemoryStream();
        await ArchiveFrameCodec.WriteJsonAsync(
            output,
            ArchiveFrameKind.ArchiveDetected,
            new ArchiveDetectedFrame("zip", false),
            default);
        await ArchiveFrameCodec.WriteJsonAsync(
            output,
            ArchiveFrameKind.ArchiveEntry,
            Entry(index: 0),
            default);
        await ArchiveFrameCodec.WriteAsync(
            output,
            ArchiveFrameKind.InspectionCompleted,
            ReadOnlyMemory<byte>.Empty,
            default);
        output.Position = 0;
        return output;
    }

    private static ArchiveEntryFrame Entry(int index) => new(
        index,
        "one.txt",
        false,
        false,
        false,
        false,
        1,
        1,
        null);

    private sealed class FakeProcessFactory(FakeProcess process) : IArchiveWorkerProcessFactory
    {
        public ProcessStartInfo? StartInfo { get; private set; }

        public IArchiveWorkerProcess Start(ProcessStartInfo startInfo)
        {
            StartInfo = startInfo;
            return process;
        }
    }

    private sealed class ThrowingProcessFactory : IArchiveWorkerProcessFactory
    {
        public IArchiveWorkerProcess Start(ProcessStartInfo startInfo) =>
            throw new InvalidOperationException("sensitive process detail");
    }

    private sealed class FakeProcess(Stream output, Stream? error = null) : IArchiveWorkerProcess
    {
        public MemoryStream StandardInput { get; } = new();

        Stream IArchiveWorkerProcess.StandardInput => StandardInput;

        public Stream StandardOutput { get; } = output;

        public Stream StandardError { get; } = error ?? new MemoryStream();

        public bool HasExited { get; private set; }

        public int ExitCode => ExitCodeValue;

        public int ExitCodeValue { get; init; }

        public long WorkingSetBytes
        {
            get
            {
                WorkingSetSamples++;
                return WorkingSetBytesValue;
            }
        }

        public long WorkingSetBytesValue { get; init; }

        public int WorkingSetSamples { get; private set; }

        public bool Killed { get; private set; }

        public ValueTask CompleteInputAsync() => ValueTask.CompletedTask;

        public ValueTask WaitForExitAsync(CancellationToken cancellationToken)
        {
            HasExited = true;
            return ValueTask.CompletedTask;
        }

        public void KillEntireProcessTree()
        {
            Killed = true;
            HasExited = true;
            if (StandardOutput is BlockingReadStream blocking)
            {
                blocking.Release();
            }
        }

        public string ReadInputAsText() =>
            System.Text.Encoding.UTF8.GetString(StandardInput.ToArray());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ImmediateWorkerDelay : IArchiveWorkerDelay
    {
        public List<TimeSpan> RequestedDelays { get; } = [];

        public async ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            RequestedDelays.Add(delay);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private sealed class BlockingReadStream : Stream
    {
        private readonly TaskCompletionSource _released = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _released.TrySetResult();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await _released.Task.WaitAsync(cancellationToken);
            return 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

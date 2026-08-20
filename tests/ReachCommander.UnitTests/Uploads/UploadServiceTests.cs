using ReachCommander.Application.Files;
using ReachCommander.Application.Uploads;
using ReachCommander.Domain.Sources;
using ReachCommander.Infrastructure.Mutations;
using ReachCommander.Infrastructure.Uploads;
using ReachCommander.UnitTests.Support;

namespace ReachCommander.UnitTests.Uploads;

public sealed class UploadServiceTests
{
    [Fact]
    public async Task Upload_stages_then_commits_multiple_files_in_wire_order()
    {
        await using var fixture = new UploadTestFixture();

        var result = await fixture.Service.UploadAsync(
            new UploadBatchCommand("media", "/Movies"),
            UploadTestFixture.Parts(("one.txt", "one"), ("empty.bin", "")),
            CancellationToken.None);

        Assert.Equal(2, result.UploadedCount);
        Assert.Equal(3, result.TotalBytes);
        Assert.Equal(["one.txt", "empty.bin"], result.Files.Select(file => file.Name));
        Assert.Equal("one", fixture.Read("Movies/one.txt"));
        Assert.Equal(string.Empty, fixture.Read("Movies/empty.bin"));
        Assert.Empty(fixture.StagingEntries("Movies"));
    }

    [Fact]
    public async Task Read_only_source_fails_before_reading_the_first_byte()
    {
        await using var fixture = new UploadTestFixture(readOnly: true);
        var stream = new CountingReadStream("content"u8.ToArray());

        await Assert.ThrowsAsync<UploadSourceReadOnlyException>(() => fixture.Service.UploadAsync(
            new UploadBatchCommand("media", "/Movies"),
            SinglePart("one.txt", stream),
            CancellationToken.None).AsTask());

        Assert.Equal(0, stream.ReadCount);
        Assert.Empty(fixture.StagingEntries("Movies"));
    }

    [Fact]
    public async Task Duplicate_batch_names_are_rejected_case_insensitively()
    {
        await using var fixture = new UploadTestFixture();

        await Assert.ThrowsAsync<UploadNameConflictException>(() => fixture.Service.UploadAsync(
            new UploadBatchCommand("media", "/Movies"),
            UploadTestFixture.Parts(("One.txt", "one"), ("one.TXT", "two")),
            CancellationToken.None).AsTask());

        Assert.False(fixture.Exists("Movies/One.txt"));
        Assert.False(fixture.Exists("Movies/one.TXT"));
        Assert.Empty(fixture.StagingEntries("Movies"));
    }

    [Fact]
    public async Task Existing_destination_rejects_the_whole_batch()
    {
        await using var fixture = new UploadTestFixture();
        fixture.Write("Movies/existing.txt", "original");

        var exception = await Assert.ThrowsAsync<UploadNameConflictException>(() => fixture.Service.UploadAsync(
            new UploadBatchCommand("media", "/Movies"),
            UploadTestFixture.Parts(("another.txt", "new"), ("EXISTING.TXT", "replacement")),
            CancellationToken.None).AsTask());

        Assert.Contains("EXISTING.TXT", exception.FileNames);
        Assert.Equal("original", fixture.Read("Movies/existing.txt"));
        Assert.False(fixture.Exists("Movies/another.txt"));
        Assert.Empty(fixture.StagingEntries("Movies"));
    }

    [Fact]
    public async Task Actual_file_size_limit_stops_streaming_and_cleans_staging()
    {
        await using var fixture = new UploadTestFixture(options: new UploadOptions
        {
            MaxFileBytes = 3,
            MaxBatchBytes = 10,
            MaxFilesPerBatch = 2,
            MaxConcurrentBatches = 1,
        });

        await Assert.ThrowsAsync<UploadFileTooLargeException>(() => fixture.Service.UploadAsync(
            new UploadBatchCommand("media", "/Movies"),
            SinglePart("large.bin", new MemoryStream("four"u8.ToArray()), declaredLength: null),
            CancellationToken.None).AsTask());

        Assert.False(fixture.Exists("Movies/large.bin"));
        Assert.Empty(fixture.StagingEntries("Movies"));
    }

    [Fact]
    public async Task Aggregate_size_and_file_count_limits_are_enforced()
    {
        await using var bytesFixture = new UploadTestFixture(options: new UploadOptions
        {
            MaxFileBytes = 3,
            MaxBatchBytes = 4,
            MaxFilesPerBatch = 3,
            MaxConcurrentBatches = 1,
        });
        await Assert.ThrowsAsync<UploadBatchTooLargeException>(() => bytesFixture.Service.UploadAsync(
            new UploadBatchCommand("media", "/Movies"),
            UploadTestFixture.Parts(("one.bin", "123"), ("two.bin", "45")),
            CancellationToken.None).AsTask());
        Assert.Empty(bytesFixture.StagingEntries("Movies"));

        await using var countFixture = new UploadTestFixture(options: new UploadOptions
        {
            MaxFileBytes = 10,
            MaxBatchBytes = 30,
            MaxFilesPerBatch = 2,
            MaxConcurrentBatches = 1,
        });
        await Assert.ThrowsAsync<UploadTooManyFilesException>(() => countFixture.Service.UploadAsync(
            new UploadBatchCommand("media", "/Movies"),
            UploadTestFixture.Parts(("one.bin", "1"), ("two.bin", "2"), ("three.bin", "3")),
            CancellationToken.None).AsTask());
        Assert.Empty(countFixture.StagingEntries("Movies"));
    }

    [Fact]
    public async Task Empty_batch_is_rejected_without_artifacts()
    {
        await using var fixture = new UploadTestFixture();

        await Assert.ThrowsAsync<UploadEmptyException>(() => fixture.Service.UploadAsync(
            new UploadBatchCommand("media", "/Movies"),
            UploadTestFixture.Parts(),
            CancellationToken.None).AsTask());

        Assert.Empty(fixture.StagingEntries("Movies"));
    }

    [Fact]
    public async Task Reserved_staging_name_collision_retries_without_deleting_the_foreign_file()
    {
        var fileSystem = new CollisionOnceUploadFileSystem();
        await using var fixture = new UploadTestFixture(fileSystem: fileSystem);

        var result = await fixture.Service.UploadAsync(
            new UploadBatchCommand("media", "/Movies"),
            UploadTestFixture.Parts(("one.txt", "one")),
            CancellationToken.None);

        Assert.Equal(1, result.UploadedCount);
        Assert.Equal("one", fixture.Read("Movies/one.txt"));
        Assert.True(File.Exists(fileSystem.ForeignStagingPath));
    }

    [Fact]
    public async Task Move_failure_compensates_committed_and_staged_files()
    {
        var fileSystem = new FaultInjectingUploadFileSystem(failOnMoveNumber: 2);
        await using var fixture = new UploadTestFixture(fileSystem: fileSystem);

        await Assert.ThrowsAsync<UploadStorageUnavailableException>(() => fixture.Service.UploadAsync(
            new UploadBatchCommand("media", "/Movies"),
            UploadTestFixture.Parts(("one.txt", "one"), ("two.txt", "two")),
            CancellationToken.None).AsTask());

        Assert.False(fixture.Exists("Movies/one.txt"));
        Assert.False(fixture.Exists("Movies/two.txt"));
        Assert.Empty(fixture.StagingEntries("Movies"));
    }

    [Fact]
    public async Task Cleanup_failure_reports_only_logical_names()
    {
        var fileSystem = new FaultInjectingUploadFileSystem(
            failOnMoveNumber: 2,
            failDeleteFileName: "one.txt");
        await using var fixture = new UploadTestFixture(fileSystem: fileSystem);

        var exception = await Assert.ThrowsAsync<UploadCleanupRequiredException>(() =>
            fixture.Service.UploadAsync(
                new UploadBatchCommand("media", "/Movies"),
                UploadTestFixture.Parts(("one.txt", "one"), ("two.txt", "two")),
                CancellationToken.None).AsTask());

        Assert.Equal(["one.txt"], exception.FileNames);
        Assert.DoesNotContain(fixture.SourceRoot, exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.StagingEntries("Movies"));
    }

    [Fact]
    public async Task Destination_appearing_during_finalization_rejects_and_compensates_the_batch()
    {
        var fileSystem = new DestinationRaceUploadFileSystem(raceOnMoveNumber: 2);
        await using var fixture = new UploadTestFixture(fileSystem: fileSystem);

        var exception = await Assert.ThrowsAsync<UploadNameConflictException>(() => fixture.Service.UploadAsync(
            new UploadBatchCommand("media", "/Movies"),
            UploadTestFixture.Parts(("one.txt", "one"), ("two.txt", "two")),
            CancellationToken.None).AsTask());

        Assert.Equal(["two.txt"], exception.FileNames);
        Assert.False(fixture.Exists("Movies/one.txt"));
        Assert.Equal("external", fixture.Read("Movies/two.txt"));
        Assert.Empty(fixture.StagingEntries("Movies"));
    }

    [Fact]
    public async Task Cancellation_during_streaming_cleans_staging_and_keeps_the_input_open()
    {
        await using var fixture = new UploadTestFixture();
        using var cancellation = new CancellationTokenSource();
        var stream = new CancelAfterFirstReadStream("content"u8.ToArray(), cancellation);

        await Assert.ThrowsAsync<UploadCancelledException>(() => fixture.Service.UploadAsync(
            new UploadBatchCommand("media", "/Movies"),
            SinglePart("one.txt", stream),
            cancellation.Token).AsTask());

        Assert.False(stream.WasDisposed);
        Assert.False(fixture.Exists("Movies/one.txt"));
        Assert.Empty(fixture.StagingEntries("Movies"));
    }

    [Fact]
    public async Task Same_directory_uploads_share_the_mutation_lock_for_the_whole_stream()
    {
        var mutationLock = new DirectoryMutationLock();
        await using var fixture = new UploadTestFixture(mutationLock: mutationLock);
        var secondService = fixture.CreateService(mutationLock: mutationLock);
        var blockingStream = new BlockingReadStream("first"u8.ToArray());
        var secondStream = new CountingReadStream("second"u8.ToArray());

        var first = fixture.Service.UploadAsync(
            new UploadBatchCommand("media", "/Movies"),
            SinglePart("first.txt", blockingStream),
            CancellationToken.None).AsTask();
        await blockingStream.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var second = secondService.UploadAsync(
            new UploadBatchCommand("media", "/Movies"),
            SinglePart("second.txt", secondStream),
            CancellationToken.None).AsTask();

        await Task.Yield();
        Assert.Equal(0, secondStream.ReadCount);
        blockingStream.ReleaseRead();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("first", fixture.Read("Movies/first.txt"));
        Assert.Equal("second", fixture.Read("Movies/second.txt"));
    }

    [Fact]
    public async Task Changed_physical_destination_before_finalization_cleans_staging()
    {
        await using var fixture = new UploadTestFixture(pathSecurityFactory: (sourceRoot, source) =>
        {
            var alternateRoot = Path.Combine(Path.GetDirectoryName(sourceRoot)!, "alternate", "Movies");
            Directory.CreateDirectory(alternateRoot);
            return new SequencedPathSecurityService(source, [
                Path.Combine(sourceRoot, "Movies"),
                Path.Combine(sourceRoot, "Movies"),
                alternateRoot,
            ]);
        });

        await Assert.ThrowsAsync<UploadStorageUnavailableException>(() => fixture.Service.UploadAsync(
            new UploadBatchCommand("media", "/Movies"),
            UploadTestFixture.Parts(("one.txt", "one")),
            CancellationToken.None).AsTask());

        Assert.False(fixture.Exists("Movies/one.txt"));
        Assert.Empty(fixture.StagingEntries("Movies"));
    }

    private static async IAsyncEnumerable<UploadFilePart> SinglePart(
        string name,
        Stream stream,
        long? declaredLength = null)
    {
        await Task.Yield();
        yield return new UploadFilePart(name, stream, declaredLength);
    }

    private sealed class CountingReadStream(byte[] buffer) : MemoryStream(buffer)
    {
        public int ReadCount { get; private set; }

        public override ValueTask<int> ReadAsync(
            Memory<byte> destination,
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return base.ReadAsync(destination, cancellationToken);
        }
    }

    private sealed class CancelAfterFirstReadStream(
        byte[] buffer,
        CancellationTokenSource cancellation) : MemoryStream(buffer)
    {
        public bool WasDisposed { get; private set; }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> destination,
            CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(destination, cancellationToken);
            if (read > 0)
            {
                cancellation.Cancel();
            }

            return read;
        }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class BlockingReadStream(byte[] buffer) : MemoryStream(buffer)
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReadEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(
            Memory<byte> destination,
            CancellationToken cancellationToken = default)
        {
            ReadEntered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return await base.ReadAsync(destination, cancellationToken);
        }

        public void ReleaseRead() => _release.TrySetResult();
    }

    private sealed class CollisionOnceUploadFileSystem : IUploadFileSystem
    {
        private readonly LocalUploadFileSystem _inner = new();
        private int _createAttempts;

        public string ForeignStagingPath { get; private set; } = string.Empty;

        public Stream CreateNewFile(string physicalPath)
        {
            if (Interlocked.Increment(ref _createAttempts) == 1)
            {
                ForeignStagingPath = physicalPath;
                File.WriteAllText(physicalPath, "foreign");
                throw new IOException("Simulated create-new collision.");
            }

            return _inner.CreateNewFile(physicalPath);
        }

        public IReadOnlyList<UploadDirectoryEntry> EnumerateDirectory(string physicalDirectory) =>
            _inner.EnumerateDirectory(physicalDirectory);

        public bool DirectoryExists(string physicalDirectory) => _inner.DirectoryExists(physicalDirectory);

        public bool FileExists(string physicalPath) => _inner.FileExists(physicalPath);

        public void MoveWithoutOverwrite(string sourcePhysicalPath, string destinationPhysicalPath) =>
            _inner.MoveWithoutOverwrite(sourcePhysicalPath, destinationPhysicalPath);

        public void DeleteFileIfExists(string physicalPath) => _inner.DeleteFileIfExists(physicalPath);

        public long? GetAvailableBytes(string physicalDirectory) => _inner.GetAvailableBytes(physicalDirectory);
    }

    private sealed class FaultInjectingUploadFileSystem(
        int failOnMoveNumber,
        string? failDeleteFileName = null) : IUploadFileSystem
    {
        private readonly LocalUploadFileSystem _inner = new();
        private int _moveCount;

        public Stream CreateNewFile(string physicalPath) => _inner.CreateNewFile(physicalPath);

        public IReadOnlyList<UploadDirectoryEntry> EnumerateDirectory(string physicalDirectory) =>
            _inner.EnumerateDirectory(physicalDirectory);

        public bool DirectoryExists(string physicalDirectory) => _inner.DirectoryExists(physicalDirectory);

        public bool FileExists(string physicalPath) => _inner.FileExists(physicalPath);

        public void MoveWithoutOverwrite(string sourcePhysicalPath, string destinationPhysicalPath)
        {
            if (Interlocked.Increment(ref _moveCount) == failOnMoveNumber)
            {
                throw new IOException("Injected move failure.");
            }

            _inner.MoveWithoutOverwrite(sourcePhysicalPath, destinationPhysicalPath);
        }

        public void DeleteFileIfExists(string physicalPath)
        {
            if (Path.GetFileName(physicalPath).Equals(failDeleteFileName, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Injected cleanup failure.");
            }

            _inner.DeleteFileIfExists(physicalPath);
        }

        public long? GetAvailableBytes(string physicalDirectory) => _inner.GetAvailableBytes(physicalDirectory);
    }

    private sealed class DestinationRaceUploadFileSystem(int raceOnMoveNumber) : IUploadFileSystem
    {
        private readonly LocalUploadFileSystem _inner = new();
        private int _moveCount;

        public Stream CreateNewFile(string physicalPath) => _inner.CreateNewFile(physicalPath);

        public IReadOnlyList<UploadDirectoryEntry> EnumerateDirectory(string physicalDirectory) =>
            _inner.EnumerateDirectory(physicalDirectory);

        public bool DirectoryExists(string physicalDirectory) => _inner.DirectoryExists(physicalDirectory);

        public bool FileExists(string physicalPath) => _inner.FileExists(physicalPath);

        public void MoveWithoutOverwrite(string sourcePhysicalPath, string destinationPhysicalPath)
        {
            if (Interlocked.Increment(ref _moveCount) == raceOnMoveNumber)
            {
                File.WriteAllText(destinationPhysicalPath, "external");
            }

            _inner.MoveWithoutOverwrite(sourcePhysicalPath, destinationPhysicalPath);
        }

        public void DeleteFileIfExists(string physicalPath) => _inner.DeleteFileIfExists(physicalPath);

        public long? GetAvailableBytes(string physicalDirectory) => _inner.GetAvailableBytes(physicalDirectory);
    }

    private sealed class SequencedPathSecurityService(
        SourceDefinition source,
        IReadOnlyList<string> physicalPaths) : IPathSecurityService
    {
        private int _index;

        public ValueTask<ResolvedSourcePath> ResolveAsync(
            string sourceId,
            string logicalPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = Math.Min(Interlocked.Increment(ref _index) - 1, physicalPaths.Count - 1);
            return ValueTask.FromResult(new ResolvedSourcePath(source, "/Movies", physicalPaths[index]));
        }

        public ValueTask<ResolvedSourcePath> ResolveChildAsync(
            string sourceId,
            string parentLogicalPath,
            string childName,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}

using System.Buffers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReachCommander.Application.Files;
using ReachCommander.Application.Sources;
using ReachCommander.Application.Uploads;
using ReachCommander.Infrastructure.Mutations;

namespace ReachCommander.Infrastructure.Uploads;

internal sealed class UploadService : IUploadService, IDisposable
{
    private const int CopyBufferSize = 80 * 1024;
    private const int MaximumStagingNameAttempts = 8;

    private readonly IPathSecurityService _pathSecurity;
    private readonly UploadFilenameValidator _filenameValidator;
    private readonly DirectoryMutationLock _directoryMutationLock;
    private readonly IUploadFileSystem _fileSystem;
    private readonly UploadOptions _options;
    private readonly ILogger<UploadService> _logger;
    private readonly SemaphoreSlim _batchSlots;

    public UploadService(
        IPathSecurityService pathSecurity,
        UploadFilenameValidator filenameValidator,
        DirectoryMutationLock directoryMutationLock,
        IUploadFileSystem fileSystem,
        IOptions<UploadOptions> options,
        ILogger<UploadService> logger)
    {
        _pathSecurity = pathSecurity;
        _filenameValidator = filenameValidator;
        _directoryMutationLock = directoryMutationLock;
        _fileSystem = fileSystem;
        _options = options.Value;
        _logger = logger;
        _batchSlots = new SemaphoreSlim(_options.MaxConcurrentBatches, _options.MaxConcurrentBatches);
    }

    public async ValueTask<UploadBatchResult> UploadAsync(
        UploadBatchCommand command,
        IAsyncEnumerable<UploadFilePart> files,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(files);

        await _batchSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await UploadWithinSlotAsync(command, files, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _batchSlots.Release();
        }
    }

    public void Dispose() => _batchSlots.Dispose();

    private async ValueTask<UploadBatchResult> UploadWithinSlotAsync(
        UploadBatchCommand command,
        IAsyncEnumerable<UploadFilePart> files,
        CancellationToken cancellationToken)
    {
        var initial = await _pathSecurity
            .ResolveAsync(command.SourceId, command.DirectoryPath, cancellationToken)
            .ConfigureAwait(false);
        EnsureWritable(initial);

        await using var mutationLease = await _directoryMutationLock
            .AcquireAsync(initial.Source.Id, initial.LogicalPath, cancellationToken)
            .ConfigureAwait(false);

        var destination = await _pathSecurity
            .ResolveAsync(initial.Source.Id, initial.LogicalPath, cancellationToken)
            .ConfigureAwait(false);
        EnsureWritable(destination);

        var staged = new List<StagedUpload>();
        var committed = new List<StagedUpload>();
        try
        {
            var batchId = Guid.NewGuid();
            var requestedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var totalBytes = 0L;
            var declaredBytes = 0L;
            var availableBytes = _fileSystem.GetAvailableBytes(destination.PhysicalPath);

            await foreach (var part in files.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (staged.Count >= _options.MaxFilesPerBatch)
                {
                    throw new UploadTooManyFilesException(_options.MaxFilesPerBatch);
                }

                if (part.Content is null)
                {
                    throw new UploadMalformedRequestException();
                }

                var fileName = _filenameValidator.Validate(part.FileName);
                if (!requestedNames.Add(fileName))
                {
                    throw new UploadNameConflictException([fileName]);
                }

                ValidateDeclaredLength(part, fileName, ref declaredBytes, availableBytes);
                var finalPath = Path.Combine(destination.PhysicalPath, fileName);
                var stagedContent = await StageAsync(
                    part.Content,
                    destination.PhysicalPath,
                    batchId,
                    staged.Count,
                    fileName,
                    totalBytes,
                    cancellationToken).ConfigureAwait(false);
                totalBytes = checked(totalBytes + stagedContent.Size);
                staged.Add(new StagedUpload(
                    fileName,
                    stagedContent.PhysicalPath,
                    finalPath,
                    JoinLogicalPath(destination.LogicalPath, fileName),
                    stagedContent.Size));
            }

            if (staged.Count == 0)
            {
                throw new UploadEmptyException();
            }

            var revalidated = await _pathSecurity
                .ResolveAsync(destination.Source.Id, destination.LogicalPath, cancellationToken)
                .ConfigureAwait(false);
            EnsureWritable(revalidated);
            if (!PhysicalPathsEqual(destination.PhysicalPath, revalidated.PhysicalPath))
            {
                throw new UploadStorageUnavailableException();
            }

            var existingNames = _fileSystem
                .EnumerateDirectory(revalidated.PhysicalPath)
                .Select(entry => entry.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var conflicts = staged
                .Where(item => existingNames.Contains(item.FileName))
                .Select(item => item.FileName)
                .ToArray();
            if (conflicts.Length > 0)
            {
                throw new UploadNameConflictException(conflicts);
            }

            _logger.LogInformation(
                "Finalizing upload batch {BatchId} to source {SourceId} directory {LogicalDirectory} with {FileCount} files and {TotalBytes} bytes.",
                batchId,
                destination.Source.Id,
                destination.LogicalPath,
                staged.Count,
                totalBytes);

            foreach (var item in staged)
            {
                try
                {
                    _fileSystem.MoveWithoutOverwrite(item.StagingPhysicalPath, item.FinalPhysicalPath);
                    committed.Add(item);
                }
                catch (IOException) when (_fileSystem.FileExists(item.FinalPhysicalPath))
                {
                    throw new UploadNameConflictException([item.FileName]);
                }
            }

            return new UploadBatchResult(
                staged.Count,
                totalBytes,
                staged.Select(item => new UploadedFile(
                    item.FileName,
                    item.FinalLogicalPath,
                    item.Size)).ToArray());
        }
        catch (OperationCanceledException)
        {
            CleanupOrThrow(staged, committed);
            throw new UploadCancelledException();
        }
        catch (UploadException)
        {
            CleanupOrThrow(staged, committed);
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            CleanupOrThrow(staged, committed);
            throw new UploadSourceReadOnlyException(destination.Source.Id);
        }
        catch (IOException)
        {
            CleanupOrThrow(staged, committed);
            throw new UploadStorageUnavailableException();
        }
        catch (Exception exception) when (
            exception is FileAccessException or SourceNotFoundException)
        {
            CleanupOrThrow(staged, committed);
            throw;
        }
    }

    private void EnsureWritable(ResolvedSourcePath resolved)
    {
        if (resolved.Source.IsReadOnly)
        {
            throw new UploadSourceReadOnlyException(resolved.Source.Id);
        }

        if (!_fileSystem.DirectoryExists(resolved.PhysicalPath))
        {
            throw new UploadStorageUnavailableException();
        }
    }

    private void ValidateDeclaredLength(
        UploadFilePart part,
        string fileName,
        ref long declaredBytes,
        long? availableBytes)
    {
        if (part.DeclaredLength is null)
        {
            return;
        }

        if (part.DeclaredLength < 0)
        {
            throw new UploadMalformedRequestException();
        }

        if (part.DeclaredLength > _options.MaxFileBytes)
        {
            throw new UploadFileTooLargeException(fileName, _options.MaxFileBytes);
        }

        if (declaredBytes > _options.MaxBatchBytes - part.DeclaredLength.Value)
        {
            throw new UploadBatchTooLargeException(_options.MaxBatchBytes);
        }

        declaredBytes += part.DeclaredLength.Value;
        if (availableBytes is not null && declaredBytes > availableBytes)
        {
            throw new UploadStorageUnavailableException();
        }
    }

    private async ValueTask<StagedContent> StageAsync(
        Stream content,
        string destinationPhysicalPath,
        Guid batchId,
        int index,
        string fileName,
        long batchBytesBeforeFile,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaximumStagingNameAttempts; attempt++)
        {
            var candidateId = attempt == 0 ? batchId : Guid.NewGuid();
            var stagingPhysicalPath = Path.Combine(
                destinationPhysicalPath,
                $".reachcommander-upload-{candidateId:N}-{index:D5}.partial");
            Stream staging;
            try
            {
                staging = _fileSystem.CreateNewFile(stagingPhysicalPath);
            }
            catch (IOException) when (_fileSystem.FileExists(stagingPhysicalPath))
            {
                continue;
            }

            try
            {
                await using (staging.ConfigureAwait(false))
                {
                    var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
                    var fileBytes = 0L;
                    try
                    {
                        while (true)
                        {
                            var read = await content
                                .ReadAsync(buffer.AsMemory(0, CopyBufferSize), cancellationToken)
                                .ConfigureAwait(false);
                            if (read == 0)
                            {
                                break;
                            }

                            if (fileBytes > _options.MaxFileBytes - read)
                            {
                                throw new UploadFileTooLargeException(fileName, _options.MaxFileBytes);
                            }

                            if (batchBytesBeforeFile + fileBytes > _options.MaxBatchBytes - read)
                            {
                                throw new UploadBatchTooLargeException(_options.MaxBatchBytes);
                            }

                            await staging
                                .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                                .ConfigureAwait(false);
                            fileBytes += read;
                        }

                        await staging.FlushAsync(cancellationToken).ConfigureAwait(false);
                        return new StagedContent(stagingPhysicalPath, fileBytes);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }
            }
            catch (Exception exception) when (
                exception is UploadException or IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                try
                {
                    _fileSystem.DeleteFileIfExists(stagingPhysicalPath);
                }
                catch (Exception cleanupException) when (
                    cleanupException is IOException or UnauthorizedAccessException)
                {
                    throw new UploadCleanupRequiredException([fileName]);
                }

                throw;
            }
        }

        throw new UploadStorageUnavailableException();
    }

    private void CleanupOrThrow(
        IReadOnlyList<StagedUpload> staged,
        IReadOnlyList<StagedUpload> committed)
    {
        var failedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in committed.Reverse())
        {
            TryDelete(item.FinalPhysicalPath, item.FileName, failedNames);
        }

        foreach (var item in staged.Reverse())
        {
            TryDelete(item.StagingPhysicalPath, item.FileName, failedNames);
        }

        if (failedNames.Count > 0)
        {
            throw new UploadCleanupRequiredException(failedNames);
        }
    }

    private void TryDelete(string physicalPath, string fileName, ISet<string> failedNames)
    {
        try
        {
            _fileSystem.DeleteFileIfExists(physicalPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failedNames.Add(fileName);
        }
    }

    private static bool PhysicalPathsEqual(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string JoinLogicalPath(string directory, string fileName) =>
        directory == "/" ? $"/{fileName}" : $"{directory}/{fileName}";

    private sealed record StagedUpload(
        string FileName,
        string StagingPhysicalPath,
        string FinalPhysicalPath,
        string FinalLogicalPath,
        long Size);

    private sealed record StagedContent(string PhysicalPath, long Size);
}

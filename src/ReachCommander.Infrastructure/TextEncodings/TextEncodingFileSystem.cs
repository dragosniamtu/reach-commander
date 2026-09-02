using System.Security.Cryptography;
using ReachCommander.Application.TextEncodings;

namespace ReachCommander.Infrastructure.TextEncodings;

internal sealed record TextFileFingerprint(
    long Length,
    DateTimeOffset ModifiedAt,
    FileAttributes Attributes,
    string Sha256);

internal sealed record TextFileSnapshot(
    string LogicalPath,
    string PhysicalPath,
    string Name,
    string Extension,
    string LogicalDirectory,
    string PhysicalDirectory,
    bool IsSymbolicLink,
    TextFileFingerprint Fingerprint,
    byte[] Bytes);

internal interface ITextEncodingFileSystem
{
    ValueTask<TextFileSnapshot> ReadSnapshotAsync(
        string logicalPath,
        string physicalPath,
        bool pathTraversedSymbolicLink,
        CancellationToken cancellationToken);

    Task WriteNewAsync(
        string physicalPath,
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken);

    void MoveFile(string sourcePhysicalPath, string destinationPhysicalPath);

    void DeleteFile(string physicalPath);

    bool FileExists(string physicalPath);

    IReadOnlyList<string> ListNames(string directoryPhysicalPath);

    void FlushDirectory(string directoryPhysicalPath);
}

internal sealed class LocalTextEncodingFileSystem : ITextEncodingFileSystem
{
    internal const long MaximumFileBytes = 32L * 1024 * 1024;

    public async ValueTask<TextFileSnapshot> ReadSnapshotAsync(
        string logicalPath,
        string physicalPath,
        bool pathTraversedSymbolicLink,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var file = new FileInfo(physicalPath);
        file.Refresh();
        if (!file.Exists || Directory.Exists(physicalPath))
        {
            throw TextEncodingException.FileNotRegular();
        }

        var initialLength = file.Length;
        var initialModifiedAt = new DateTimeOffset(file.LastWriteTimeUtc);
        var initialAttributes = file.Attributes;
        var isSymbolicLink = pathTraversedSymbolicLink ||
            file.LinkTarget is not null ||
            initialAttributes.HasFlag(FileAttributes.ReparsePoint);
        if (isSymbolicLink)
        {
            throw TextEncodingException.SymbolicLinkRejected();
        }

        if (initialLength > MaximumFileBytes)
        {
            throw TextEncodingException.FileTooLarge();
        }

        var bytes = new byte[(int)initialLength];
        await using (var stream = new FileStream(
                         physicalPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         bufferSize: 64 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await stream.ReadExactlyAsync(bytes, cancellationToken);
        }

        file.Refresh();
        if (!file.Exists ||
            file.Length != initialLength ||
            file.LastWriteTimeUtc != initialModifiedAt.UtcDateTime ||
            file.Attributes != initialAttributes)
        {
            throw TextEncodingException.FileNotRegular();
        }

        var logicalDirectory = LogicalDirectory(logicalPath);
        var physicalDirectory = Path.GetDirectoryName(physicalPath);
        if (physicalDirectory is null)
        {
            throw TextEncodingException.FileNotRegular();
        }

        return new TextFileSnapshot(
            logicalPath,
            physicalPath,
            file.Name,
            file.Extension,
            logicalDirectory,
            physicalDirectory,
            IsSymbolicLink: false,
            new TextFileFingerprint(
                initialLength,
                initialModifiedAt,
                initialAttributes,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()),
            bytes);
    }

    public async Task WriteNewAsync(
        string physicalPath,
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            physicalPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(contents, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    public void MoveFile(string sourcePhysicalPath, string destinationPhysicalPath) =>
        File.Move(sourcePhysicalPath, destinationPhysicalPath, overwrite: false);

    public void DeleteFile(string physicalPath) => File.Delete(physicalPath);

    public bool FileExists(string physicalPath) => File.Exists(physicalPath);

    public IReadOnlyList<string> ListNames(string directoryPhysicalPath) =>
        new DirectoryInfo(directoryPhysicalPath)
            .EnumerateFileSystemInfos()
            .Select(entry => entry.Name)
            .ToArray();

    public void FlushDirectory(string directoryPhysicalPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            using var handle = File.OpenHandle(
                directoryPhysicalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                FileOptions.None);
            RandomAccess.FlushToDisk(handle);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
    }

    private static string LogicalDirectory(string logicalPath)
    {
        var separator = logicalPath.LastIndexOf('/');
        return separator <= 0 ? "/" : logicalPath[..separator];
    }
}

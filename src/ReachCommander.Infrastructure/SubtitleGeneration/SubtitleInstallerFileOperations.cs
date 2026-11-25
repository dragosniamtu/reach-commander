using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ReachCommander.Infrastructure.SubtitleGeneration;

// Fault injection replaces only filesystem mutations; tests use real files and real persisted journals.
internal class SubtitleInstallerFileOperations
{
    public virtual async Task WriteNewAsync(string path, ReadOnlyMemory<byte> contents, CancellationToken cancellationToken)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None, BufferSize = 1,
            Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
        };
        await using var stream = new FileStream(path, options);
        try
        {
            await WriteContentsAsync(stream, contents, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            // The handle was opened with CreateNew and remains exclusively held. Record the actual bytes from
            // that owned handle, never by reopening an attacker-replaceable path. The installer persists this
            // proof before cleaning up, and rechecks that the current path still has the proven content hash.
            stream.Position = 0;
            var ownedHash = Convert.ToHexString(SHA256.HashData(stream));
            throw new SubtitleStageWriteException(path, ownedHash, exception);
        }
    }

    protected virtual ValueTask WriteContentsAsync(FileStream stream, ReadOnlyMemory<byte> contents, CancellationToken cancellationToken) =>
        stream.WriteAsync(contents, cancellationToken);

    public virtual void Move(string source, string destination) => File.Move(source, destination, overwrite: false);
    public virtual void Delete(string path) => File.Delete(path);

    public virtual void FlushDirectory(string directory)
    {
        // Linux needs directory entries persisted as well as file contents for power-loss recovery.
        // Windows has no equivalent supported directory fsync; stage/journal writes use WriteThrough.
        if (!OperatingSystem.IsLinux()) return;
        var descriptor = Open(directory, 0x10000 | 0x80000); // O_RDONLY | O_DIRECTORY | O_CLOEXEC
        if (descriptor < 0) throw Failure();
        try { if (Fsync(descriptor) != 0) throw Failure(); }
        finally { Close(descriptor); }
    }

    private static IOException Failure() => new("Could not durably flush subtitle storage.", new Win32Exception(Marshal.GetLastPInvokeError()));
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string pathname, int flags);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int fd);
    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int fd);
}

internal sealed class SubtitleStageWriteException(string path, string ownedHash, Exception inner)
    : IOException("A subtitle staging file could not be fully written.", inner)
{
    public string StagePath { get; } = path;
    public string OwnedHash { get; } = ownedHash;
}

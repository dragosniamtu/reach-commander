using Microsoft.Extensions.Options;
using ReachCommander.Application.Archives;
using ReachCommander.ArchiveProtocol;
using ReachCommander.Infrastructure.Archives.Worker;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ReachCommander.Infrastructure.Archives.Extraction;

internal interface IArchiveExtractionRuntimeFileSystem : IArchiveExtractionFileSystem
{
    IDisposable OpenReadShared(string physicalPath);

    ArchiveStagingIdentity CreateOwnedStagingDirectory(string physicalPath);

    bool VerifyOwnedStaging(ArchiveStagingIdentity identity);

    void CreateDirectory(string physicalPath);

    bool IsRealDirectory(string physicalPath);

    void VerifyTreeHasNoLinks(ArchiveStagingIdentity identity);

    Stream CreateFileNew(string physicalPath);

    void TrySetLastWriteTimeUtc(string physicalPath, DateTimeOffset value);

    void MoveNew(string sourcePhysicalPath, string destinationPhysicalPath);

    void DeleteOwnedDirectoryTree(ArchiveStagingIdentity identity);
}

internal sealed record ArchiveDirectoryFileId(ulong Volume, ulong Index, ulong Mount);

internal sealed class ArchiveStagingIdentity : IDisposable
{
    private IDisposable? _directoryLease;
    private FileStream? _markerLease;

    internal ArchiveStagingIdentity(
        string rootPath,
        ArchiveDirectoryFileId directoryId,
        IDisposable directoryLease)
    {
        RootPath = rootPath;
        RecoveryPath = rootPath;
        DirectoryId = directoryId;
        _directoryLease = directoryLease;
    }

    public string RootPath { get; }

    public string RecoveryPath { get; private set; }

    internal ArchiveDirectoryFileId DirectoryId { get; }

    internal string? MarkerPath { get; private set; }

    internal byte[]? Token { get; private set; }

    internal bool HasDirectoryLease => _directoryLease is not null;

    internal bool HasMarkerLease => _markerLease is not null;

    internal void AttachMarker(string markerPath, byte[] token, FileStream markerLease)
    {
        MarkerPath = markerPath;
        Token = token;
        _markerLease = markerLease;
    }

    internal void UpdateRecoveryPath(string recoveryPath) =>
        RecoveryPath = recoveryPath;

    public void Dispose()
    {
        Interlocked.Exchange(ref _markerLease, null)?.Dispose();
        Interlocked.Exchange(ref _directoryLease, null)?.Dispose();
    }
}

internal sealed class ArchiveStagingCreationException(
    ArchiveStagingIdentity? identity,
    Exception innerException)
    : Exception("The staging directory could not establish ownership.", innerException)
{
    public ArchiveStagingIdentity? Identity { get; } = identity;
}

internal sealed class LocalArchiveExtractionRuntimeFileSystem :
    LocalArchiveExtractionFileSystem,
    IArchiveExtractionRuntimeFileSystem
{
    private const string OwnershipMarkerName = ".reachcommander-owner";

    public IDisposable OpenReadShared(string physicalPath) => new FileStream(
        physicalPath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 1,
        FileOptions.SequentialScan);

    public ArchiveStagingIdentity CreateOwnedStagingDirectory(string physicalPath)
    {
        physicalPath = Path.GetFullPath(physicalPath);
        CreateDirectoryAtomically(physicalPath);
        ArchiveStagingIdentity? identity = null;
        FileStream? markerLease = null;
        try
        {
            var opened = OpenDirectoryIdentity(physicalPath);
            identity = new ArchiveStagingIdentity(
                physicalPath,
                opened.Id,
                opened.Lease);
            var markerPath = Path.Combine(physicalPath, OwnershipMarkerName);
            var token = RandomNumberGenerator.GetBytes(32);
            markerLease = new FileStream(
                markerPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.Read | FileShare.Delete,
                bufferSize: token.Length,
                FileOptions.WriteThrough);
            markerLease.Write(token);
            markerLease.Flush(flushToDisk: true);
            markerLease.Position = 0;
            identity.AttachMarker(markerPath, token, markerLease);
            markerLease = null;
            if (!VerifyOwnedStaging(identity))
            {
                throw new IOException("Staging ownership could not be verified.");
            }

            return identity;
        }
        catch (Exception exception)
        {
            markerLease?.Dispose();
            if (identity is null)
            {
                try
                {
                    Directory.Delete(physicalPath, recursive: false);
                }
                catch
                {
                }
            }

            throw new ArchiveStagingCreationException(identity, exception);
        }
    }

    public bool VerifyOwnedStaging(ArchiveStagingIdentity identity) =>
        VerifyDirectoryIdentity(identity) &&
        IsRealDirectory(identity.RootPath) &&
        VerifyMarker(identity);

    public void CreateDirectory(string physicalPath)
    {
        if (File.Exists(physicalPath))
        {
            throw new IOException("An extraction directory collides with a file.");
        }

        Directory.CreateDirectory(physicalPath);
        if (!IsRealDirectory(physicalPath))
        {
            throw new IOException("An extraction directory is not a real directory.");
        }
    }

    public bool IsRealDirectory(string physicalPath)
    {
        var directory = new DirectoryInfo(physicalPath);
        directory.Refresh();
        return directory.Exists &&
            directory.LinkTarget is null &&
            !directory.Attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    public void VerifyTreeHasNoLinks(ArchiveStagingIdentity identity)
    {
        if (!VerifyOwnedStaging(identity))
        {
            throw new IOException("Staging ownership changed.");
        }

        var pending = new Stack<string>();
        pending.Push(identity.RootPath);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!IsRealDirectory(current))
            {
                throw new IOException("The extraction tree contains a linked directory.");
            }

            foreach (var path in Directory.EnumerateFileSystemEntries(current))
            {
                var info = File.GetAttributes(path);
                var entry = info.HasFlag(FileAttributes.Directory)
                    ? (FileSystemInfo)new DirectoryInfo(path)
                    : new FileInfo(path);
                entry.Refresh();
                if (entry.LinkTarget is not null || info.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new IOException("The extraction tree contains a linked entry.");
                }

                if (entry is DirectoryInfo)
                {
                    pending.Push(path);
                }
            }
        }
    }

    public Stream CreateFileNew(string physicalPath) => new FileStream(
        physicalPath,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None,
        ArchiveFrameCodec.MaxDataPayloadBytes,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    public void TrySetLastWriteTimeUtc(string physicalPath, DateTimeOffset value)
    {
        try
        {
            var file = new FileInfo(physicalPath);
            file.Refresh();
            if (!file.Exists ||
                file.LinkTarget is not null ||
                file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return;
            }

            File.SetLastWriteTimeUtc(physicalPath, value.UtcDateTime);
        }
        catch (Exception exception) when (
            exception is ArgumentOutOfRangeException or IOException or UnauthorizedAccessException)
        {
        }
    }

    public void MoveNew(string sourcePhysicalPath, string destinationPhysicalPath)
    {
        if (Directory.Exists(destinationPhysicalPath) || File.Exists(destinationPhysicalPath))
        {
            throw new IOException("The extraction destination already exists.");
        }

        if (Directory.Exists(sourcePhysicalPath))
        {
            Directory.Move(sourcePhysicalPath, destinationPhysicalPath);
        }
        else
        {
            File.Move(sourcePhysicalPath, destinationPhysicalPath, overwrite: false);
        }
    }

    public void DeleteOwnedDirectoryTree(ArchiveStagingIdentity identity)
    {
        if (!VerifyDirectoryIdentity(identity))
        {
            throw new IOException("Refusing to delete an unowned staging path.");
        }

        var parent = Path.GetDirectoryName(identity.RootPath)
            ?? throw new IOException("The staging parent is invalid.");
        var quarantine = Path.Combine(
            parent,
            $".reachcommander-cleanup-{Convert.ToHexString(RandomNumberGenerator.GetBytes(16))}.partial");
        identity.Dispose();
        Directory.Move(identity.RootPath, quarantine);
        identity.UpdateRecoveryPath(quarantine);
        if (!TryReadPathIdentity(quarantine, out var quarantinedId) ||
            quarantinedId != identity.DirectoryId)
        {
            throw new IOException("The quarantined staging identity changed.");
        }

        Directory.Delete(quarantine, recursive: true);
    }

    private static bool VerifyMarker(ArchiveStagingIdentity identity)
    {
        try
        {
            if (!identity.HasMarkerLease ||
                identity.MarkerPath is null ||
                identity.Token is null)
            {
                return false;
            }

            var marker = new FileInfo(identity.MarkerPath);
            marker.Refresh();
            if (!marker.Exists ||
                marker.Length != identity.Token.Length ||
                marker.LinkTarget is not null ||
                marker.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }

            using var stream = new FileStream(
                identity.MarkerPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: identity.Token.Length,
                FileOptions.SequentialScan);
            var actual = new byte[identity.Token.Length];
            stream.ReadExactly(actual);
            return stream.ReadByte() == -1 &&
                CryptographicOperations.FixedTimeEquals(actual, identity.Token);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool VerifyDirectoryIdentity(ArchiveStagingIdentity identity) =>
        identity.HasDirectoryLease &&
        TryReadPathIdentity(identity.RootPath, out var current) &&
        current == identity.DirectoryId;

    private static (ArchiveDirectoryFileId Id, IDisposable Lease) OpenDirectoryIdentity(
        string physicalPath)
    {
        if (OperatingSystem.IsWindows())
        {
            var handle = OpenWindowsDirectory(physicalPath);
            if (handle.IsInvalid || !TryGetWindowsIdentity(handle, out var id))
            {
                handle.Dispose();
                throw new IOException("The staging directory identity could not be opened.");
            }

            return (id, handle);
        }

        if (OperatingSystem.IsLinux())
        {
            var descriptor = OpenUnixDirectory(
                physicalPath,
                UnixOpenDirectory | UnixOpenCloseOnExec);
            if (descriptor < 0 || !TryGetLinuxIdentity(descriptor, out var id))
            {
                if (descriptor >= 0)
                {
                    _ = CloseUnix(descriptor);
                }

                throw new IOException("The staging directory identity could not be opened.");
            }

            return (id, new UnixDirectoryLease(descriptor));
        }

        throw new PlatformNotSupportedException(
            "Staging identity is supported on Windows and Linux.");
    }

    private static bool TryReadPathIdentity(
        string physicalPath,
        out ArchiveDirectoryFileId identity)
    {
        if (OperatingSystem.IsWindows())
        {
            using var handle = OpenWindowsDirectory(physicalPath);
            if (!handle.IsInvalid)
            {
                return TryGetWindowsIdentity(handle, out identity);
            }

            identity = default!;
            return false;
        }

        if (OperatingSystem.IsLinux())
        {
            return TryGetLinuxPathIdentity(physicalPath, out identity);
        }

        identity = default!;
        return false;
    }

    private static SafeFileHandle OpenWindowsDirectory(string physicalPath) =>
        CreateFileWindows(
            physicalPath,
            WindowsReadAttributes,
            WindowsShareRead | WindowsShareWrite | WindowsShareDelete,
            IntPtr.Zero,
            WindowsOpenExisting,
            WindowsBackupSemantics,
            IntPtr.Zero);

    private static bool TryGetWindowsIdentity(
        SafeFileHandle handle,
        out ArchiveDirectoryFileId identity)
    {
        if (GetFileInformationByHandle(handle, out var information))
        {
            identity = new ArchiveDirectoryFileId(
                information.VolumeSerialNumber,
                ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow,
                0);
            return true;
        }

        identity = default!;
        return false;
    }

    private static bool TryGetLinuxIdentity(
        int descriptor,
        out ArchiveDirectoryFileId identity) =>
        TryStatx(descriptor, string.Empty, LinuxAtEmptyPath, out identity);

    private static bool TryGetLinuxPathIdentity(
        string physicalPath,
        out ArchiveDirectoryFileId identity) =>
        TryStatx(LinuxAtCurrentWorkingDirectory, physicalPath, LinuxAtSymlinkNoFollow, out identity);

    private static bool TryStatx(
        int directoryDescriptor,
        string path,
        int flags,
        out ArchiveDirectoryFileId identity)
    {
        var buffer = Marshal.AllocHGlobal(LinuxStatxBufferBytes);
        try
        {
            if (StatxUnix(
                    directoryDescriptor,
                    path,
                    flags,
                    LinuxStatxBasicStats | LinuxStatxMountId,
                    buffer) != 0)
            {
                identity = default!;
                return false;
            }

            var inode = unchecked((ulong)Marshal.ReadInt64(buffer, 32));
            var deviceMajor = unchecked((uint)Marshal.ReadInt32(buffer, 136));
            var deviceMinor = unchecked((uint)Marshal.ReadInt32(buffer, 140));
            var mountId = unchecked((ulong)Marshal.ReadInt64(buffer, 144));
            identity = new ArchiveDirectoryFileId(
                ((ulong)deviceMajor << 32) | deviceMinor,
                inode,
                mountId);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void CreateDirectoryAtomically(string physicalPath)
    {
        bool created;
        if (OperatingSystem.IsWindows())
        {
            created = CreateDirectoryWindows(physicalPath, IntPtr.Zero);
        }
        else if (OperatingSystem.IsLinux())
        {
            created = CreateDirectoryUnix(physicalPath, Convert.ToUInt32("700", 8)) == 0;
        }
        else
        {
            throw new PlatformNotSupportedException(
                "Atomic staging-directory creation is supported on Windows and Linux.");
        }

        if (!created)
        {
            throw new IOException(
                "The staging directory could not be created exclusively.",
                Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryWindows(string path, IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "mkdir", SetLastError = true)]
    private static extern int CreateDirectoryUnix(string path, uint mode);

    private const uint WindowsReadAttributes = 0x80;
    private const uint WindowsShareRead = 0x1;
    private const uint WindowsShareWrite = 0x2;
    private const uint WindowsShareDelete = 0x4;
    private const uint WindowsOpenExisting = 3;
    private const uint WindowsBackupSemantics = 0x02000000;
    private const int UnixOpenDirectory = 0x10000;
    private const int UnixOpenCloseOnExec = 0x80000;
    private const int LinuxAtCurrentWorkingDirectory = -100;
    private const int LinuxAtSymlinkNoFollow = 0x100;
    private const int LinuxAtEmptyPath = 0x1000;
    private const uint LinuxStatxBasicStats = 0x7ff;
    private const uint LinuxStatxMountId = 0x1000;
    private const int LinuxStatxBufferBytes = 256;

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsFileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsFileInformation
    {
        public uint FileAttributes;
        public WindowsFileTime CreationTime;
        public WindowsFileTime LastAccessTime;
        public WindowsFileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileWindows(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out WindowsFileInformation information);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenUnixDirectory(string path, int flags);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int CloseUnix(int descriptor);

    [DllImport("libc", EntryPoint = "statx", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int StatxUnix(
        int directoryDescriptor,
        string path,
        int flags,
        uint mask,
        IntPtr buffer);

    private sealed class UnixDirectoryLease(int descriptor) : IDisposable
    {
        private int _descriptor = descriptor;

        public void Dispose()
        {
            var owned = Interlocked.Exchange(ref _descriptor, -1);
            if (owned >= 0)
            {
                _ = CloseUnix(owned);
            }
        }
    }
}

internal sealed class ArchiveStagingWriter : IArchiveEntrySink, IAsyncDisposable
{
    private readonly ArchiveExtractionPlan _plan;
    private readonly ArchiveStagingIdentity _identity;
    private readonly IArchiveExtractionRuntimeFileSystem _fileSystem;
    private readonly ArchiveOptions _options;
    private readonly Action<int, long, string?> _reportProgress;
    private readonly Dictionary<int, PlannedArchiveFile> _filesByIndex;
    private readonly HashSet<int> _started = [];
    private PlannedArchiveFile? _current;
    private Stream? _currentStream;
    private long _currentBytes;
    private long _totalBytes;
    private int _completedFiles;

    public ArchiveStagingWriter(
        ArchiveExtractionPlan plan,
        ArchiveStagingIdentity identity,
        IArchiveExtractionRuntimeFileSystem fileSystem,
        IOptions<ArchiveOptions> options,
        Action<int, long, string?> reportProgress)
    {
        _plan = plan;
        _identity = identity;
        StagingRoot = identity.RootPath;
        _fileSystem = fileSystem;
        _options = options.Value;
        _reportProgress = reportProgress;
        _filesByIndex = plan.Files.ToDictionary(file => file.WorkerEntryIndex);
    }

    public string StagingRoot { get; }

    public void Prepare()
    {
        VerifyStagingRoot();
        foreach (var relativePath in _plan.Directories
                     .OrderBy(path => path.Count(character => character == '/'))
                     .ThenBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            VerifyStagingRoot();
            var physicalPath = ResolveUnderStaging(relativePath);
            VerifyAncestors(relativePath);
            _fileSystem.CreateDirectory(physicalPath);
        }
    }

    public ValueTask StartAsync(int entryIndex, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_current is not null ||
            !_filesByIndex.TryGetValue(entryIndex, out var file) ||
            !_started.Add(entryIndex))
        {
            throw new ArchiveWorkerFailedException();
        }

        VerifyStagingRoot();
        VerifyAncestors(file.RelativeOutputPath);
        var physicalPath = ResolveUnderStaging(file.RelativeOutputPath);
        _currentStream = _fileSystem.CreateFileNew(physicalPath);
        _current = file;
        _currentBytes = 0;
        _reportProgress(_completedFiles, _totalBytes, Path.GetFileName(file.RelativeOutputPath));
        return ValueTask.CompletedTask;
    }

    public async ValueTask WriteAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        if (_current is null || _currentStream is null || data.IsEmpty)
        {
            throw new ArchiveWorkerFailedException();
        }

        try
        {
            _currentBytes = checked(_currentBytes + data.Length);
            _totalBytes = checked(_totalBytes + data.Length);
        }
        catch (OverflowException)
        {
            throw Limit();
        }

        if (_currentBytes > _options.MaxSingleExtractedFileBytes ||
            _totalBytes > _options.MaxTotalExtractedBytes ||
            (_current.DeclaredCompressedSize is { } compressed &&
             _currentBytes > 0 &&
             (compressed == 0 ||
              _currentBytes / (double)compressed > _options.MaxExpansionRatio)))
        {
            throw Limit();
        }

        VerifyStagingRoot();
        VerifyAncestors(_current.RelativeOutputPath);
        await _currentStream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask EndAsync(
        int entryIndex,
        long actualBytes,
        CancellationToken cancellationToken)
    {
        if (_current is null || _currentStream is null ||
            _current.WorkerEntryIndex != entryIndex ||
            actualBytes != _currentBytes)
        {
            throw new ArchiveWorkerFailedException();
        }

        var file = _current;
        var stream = _currentStream;
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        await stream.DisposeAsync().ConfigureAwait(false);
        _currentStream = null;
        _current = null;
        _completedFiles++;
        if (file.ModifiedAt is { } modified)
        {
            _fileSystem.TrySetLastWriteTimeUtc(
                ResolveUnderStaging(file.RelativeOutputPath),
                modified);
        }
    }

    public ValueTask ProgressAsync(
        int completedFiles,
        long actualBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_current is not null ||
            completedFiles != _completedFiles ||
            actualBytes != _totalBytes)
        {
            throw new ArchiveWorkerFailedException();
        }

        _reportProgress(completedFiles, actualBytes, null);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_currentStream is not null)
        {
            await _currentStream.DisposeAsync().ConfigureAwait(false);
            _currentStream = null;
            _current = null;
        }
    }

    internal string ResolveUnderStaging(string relativePath)
    {
        var combined = Path.GetFullPath(Path.Combine(
            StagingRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(StagingRoot, combined);
        if (Path.IsPathRooted(relative) ||
            relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new ArchiveEntryUnsafeException();
        }

        return combined;
    }

    private void VerifyAncestors(string relativePath)
    {
        var components = relativePath.Split('/');
        var current = StagingRoot;
        for (var index = 0; index < components.Length - 1; index++)
        {
            current = Path.Combine(current, components[index]);
            if (!_fileSystem.IsRealDirectory(current))
            {
                throw new ArchiveEntryUnsafeException();
            }
        }
    }

    private void VerifyStagingRoot()
    {
        if (!_fileSystem.VerifyOwnedStaging(_identity))
        {
            throw new ArchiveEntryUnsafeException();
        }
    }

    private static ArchiveLimitExceededException Limit() => new(
        "Archive extraction exceeded a configured runtime size or expansion limit.");
}

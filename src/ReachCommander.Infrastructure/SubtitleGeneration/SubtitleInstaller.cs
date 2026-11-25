using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReachCommander.Application.Files;
using ReachCommander.Application.MediaPreviews;
using ReachCommander.Application.Sources;
using ReachCommander.Application.SubtitleGeneration;
using ReachCommander.Infrastructure.Authentication;
using ReachCommander.Infrastructure.FileOperations.Persistence;
using ReachCommander.Infrastructure.MediaPreviews;
using ReachCommander.Infrastructure.Mutations;

namespace ReachCommander.Infrastructure.SubtitleGeneration;

internal sealed class SubtitleInstaller(
    AuthenticationDataPaths paths,
    IPathSecurityService pathSecurity,
    DirectoryMutationLock mutationLock,
    IOptions<SubtitleGenerationOptions> options,
    ILogger<SubtitleInstaller> logger,
    SubtitleInstallerFileOperations? fileOperations = null) : ISubtitleInstaller
{
    private const int MaximumSubtitleBytes = 8 * 1024 * 1024;
    private const int MaximumJournalBytes = 16 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private readonly SubtitleInstallerFileOperations _files = fileOperations ?? new();
    private readonly string _transactions = Path.Combine(paths.RootPath, "subtitle-generation", "transactions");
    private readonly string _receipts = Path.Combine(paths.RootPath, "subtitle-generation", "receipts");

    public async Task<bool> IsAlreadyGeneratedAsync(SubtitleVideo video, bool english, bool romanian, CancellationToken cancellationToken)
    {
        try
        {
            await using var lease = await mutationLock.AcquireAsync(video.SourceId, video.DirectoryLogicalPath, cancellationToken);
            EnsureStorage();
            var resolved = await ValidateVideoAsync(video, cancellationToken);
            if (PendingTransactions(video).Length != 0) return false;
            var path = ReceiptPath(video);
            if (!Exists(path)) return false;
            var receipt = await ReadAsync<Receipt>(path, cancellationToken);
            if (receipt.Version != 1 || receipt.Video != video || receipt.Outputs is null || receipt.Outputs.Any(o => o is null) ||
                receipt.English != english || receipt.Romanian != romanian ||
                !PathsEqual(receipt.SourceRoot, resolved.Source.RootPath)) return false;
            var names = SubtitleNaming.OutputNames(Path.GetFileName(video.PhysicalPath), english, romanian);
            if (receipt.Outputs.Length != names.Length || !receipt.Outputs.Select(o => o.Name).SequenceEqual(names)) return false;
            foreach (var output in receipt.Outputs)
                if (!await HasHashAsync(Child(video, output.Name), output.Hash, cancellationToken)) return false;
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (IsRecoverable(exception)) { return false; }
    }

    public async Task InstallAsync(SubtitleVideo video, bool english, bool romanian, SubtitleEngineResult result, CancellationToken cancellationToken)
    {
        // Validate every language before any sidecar or journal mutation.
        var names = SubtitleNaming.OutputNames(Path.GetFileName(video.PhysicalPath), english, romanian);
        var englishDocument = ValidateSrt(result.EnglishSrt);
        var primary = englishDocument.Bytes;
        byte[]? romanianBytes = null;
        if (romanian)
        {
            var translated = ValidateSrt(result.RomanianSrt);
            if (!englishDocument.Document.Cues.Select(c => (c.StartMilliseconds, c.EndMilliseconds))
                .SequenceEqual(translated.Document.Cues.Select(c => (c.StartMilliseconds, c.EndMilliseconds))))
                throw InvalidOutput();
            romanianBytes = translated.Bytes;
            if (!english) primary = romanianBytes;
        }
        byte[][] contents = english && romanian ? [primary, romanianBytes!] : [primary];

        await using var lease = await mutationLock.AcquireAsync(video.SourceId, video.DirectoryLogicalPath, cancellationToken);
        EnsureStorage();
        var resolved = await ValidateVideoAsync(video, cancellationToken);
        foreach (var pending in PendingTransactions(video))
            await TryRecoverAsync(pending, cancellationToken, alreadyLocked: true);
        if (PendingTransactions(video).Length != 0) throw RecoveryRequired();

        var directory = Path.GetDirectoryName(video.PhysicalPath)!;
        var directoryNames = Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName).Cast<string>().ToArray();
        var ownedNames = SubtitleNaming.AssociatedSidecars(Path.GetFileName(video.PhysicalPath),
            directoryNames.Concat(names.Where(name => !directoryNames.Contains(name, StringComparer.OrdinalIgnoreCase))).ToArray());
        var associated = ownedNames.Where(n => directoryNames.Contains(n, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (associated.Length > 10_000) throw new SubtitleGenerationException("subtitle_too_many_sidecars", "Too many existing subtitles in this directory.");
        // An output may be owned by another video (for example Movie.ro.mkv); never overwrite or rename it.
        foreach (var name in names)
            if (!ownedNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                throw new SubtitleGenerationException("subtitle_ownership_ambiguous", "A subtitle output is owned by another file.");

        var occupied = new HashSet<string>(directoryNames, StringComparer.OrdinalIgnoreCase);
        var originals = new List<Original>();
        var sequence = 0;
        foreach (var name in associated)
        {
            var hash = await HashAsync(Child(video, name), cancellationToken);
            originals.Add(new(name, SubtitleNaming.BackupName(name, occupied, ref sequence), hash));
        }
        var id = Guid.NewGuid();
        var outputs = names.Select((name, index) => new Output(name,
            $".reachcommander-generated-subtitle-{id:N}-{index}.partial", Hash(contents[index]))).ToArray();
        var journal = new Transaction(1, id, video, Path.GetFullPath(resolved.Source.RootPath), english, romanian,
            "prepared", originals.ToArray(), outputs);
        var journalPath = TransactionPath(journal);
        EnsureFreeSpace(directory, contents.Sum(c => (long)c.Length));
        await SaveAsync(journalPath, journal, cancellationToken);
        try
        {
            for (var index = 0; index < outputs.Length; index++)
            {
                await ValidateVideoAsync(video, cancellationToken);
                var stage = Child(video, outputs[index].Stage);
                EnsureAbsent(stage);
                await _files.WriteNewAsync(stage, contents[index], cancellationToken);
                RequireHash(stage, outputs[index].Hash);
            }
            _files.FlushDirectory(directory);
            cancellationToken.ThrowIfCancellationRequested();
            // Persist intent before the first rename; infer completed moves from both names and hashes on restart.
            journal = journal with { State = "publishing" };
            await SaveAsync(journalPath, journal, cancellationToken);
            foreach (var original in originals)
            {
                await ValidateVideoAsync(video, cancellationToken);
                RequireHash(Child(video, original.Name), original.Hash);
                EnsureAbsent(Child(video, original.Backup));
                _files.Move(Child(video, original.Name), Child(video, original.Backup));
                _files.FlushDirectory(directory);
            }
            foreach (var output in outputs)
            {
                await ValidateVideoAsync(video, cancellationToken);
                RequireHash(Child(video, output.Stage), output.Hash);
                EnsureAbsent(Child(video, output.Name));
                _files.Move(Child(video, output.Stage), Child(video, output.Name));
                _files.FlushDirectory(directory);
            }
            await ValidateVideoAsync(video, cancellationToken);
            foreach (var output in outputs) RequireHash(Child(video, output.Name), output.Hash);
            journal = journal with { State = "committed" };
            await SaveAsync(journalPath, journal, CancellationToken.None);
            await CompleteAsync(journal, journalPath, CancellationToken.None);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            if (journal.State == "committed") throw RecoveryRequired();
            try
            {
                if (exception is SubtitleStageWriteException partial && journal.State == "prepared")
                {
                    journal = journal with
                    {
                        Outputs = journal.Outputs.Select(output => PathsEqual(Child(video, output.Stage), partial.StagePath)
                            ? output with { PartialHash = partial.OwnedHash } : output).ToArray(),
                    };
                    await SaveAsync(journalPath, journal, CancellationToken.None);
                }
                await RollbackAsync(journal, journalPath, CancellationToken.None);
            }
            catch (Exception rollbackException) when (IsRecoverable(rollbackException))
            {
                logger.LogWarning(rollbackException, "Subtitle transaction {TransactionId} requires recovery.", id);
                throw RecoveryRequired();
            }
            if (exception is OperationCanceledException) throw;
            if (exception is SubtitleStageWriteException { InnerException: OperationCanceledException cancelled }) throw cancelled;
            throw new SubtitleGenerationException("subtitle_install_failed", "Subtitles could not be installed. Existing subtitles were restored.");
        }
    }

    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        string[] journals;
        try
        {
            EnsureStorage();
            journals = Directory.GetFiles(_transactions, "*.json");
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            logger.LogWarning(exception, "Subtitle recovery storage is unavailable. Installation remains disabled until storage is accessible.");
            return;
        }
        foreach (var journal in journals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await TryRecoverAsync(journal, cancellationToken, alreadyLocked: false);
        }
    }

    private async Task TryRecoverAsync(string path, CancellationToken cancellationToken, bool alreadyLocked)
    {
        try
        {
            var transaction = await ReadAsync<Transaction>(path, cancellationToken);
            ValidateTransaction(transaction, path);
            IAsyncDisposable? lease = null;
            try
            {
                if (!alreadyLocked) lease = await mutationLock.AcquireAsync(
                    transaction.Video.SourceId, transaction.Video.DirectoryLogicalPath, cancellationToken);
                var resolved = await ValidateVideoAsync(transaction.Video, cancellationToken);
                if (!PathsEqual(resolved.Source.RootPath, transaction.SourceRoot)) throw RecoveryRequired();
                if (transaction.State == "committed") await CompleteAsync(transaction, path, cancellationToken);
                else await RollbackAsync(transaction, path, cancellationToken);
            }
            finally { if (lease is not null) await lease.DisposeAsync(); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // A retained journal blocks only this video; independent paths remain available.
            logger.LogWarning(exception, "An interrupted subtitle transaction could not be recovered: {JournalName}", Path.GetFileName(path));
        }
    }

    private async Task RollbackAsync(Transaction transaction, string journalPath, CancellationToken cancellationToken)
    {
        var video = transaction.Video;
        var resolved = await ValidateVideoAsync(video, cancellationToken);
        if (!PathsEqual(resolved.Source.RootPath, transaction.SourceRoot)) throw RecoveryRequired();
        var moved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var generated = new List<Output>();
        // Preflight the whole transaction before removing anything; edited or unknown content is never deleted.
        foreach (var original in transaction.Originals)
        {
            if (Exists(Child(video, original.Backup)))
            {
                if (transaction.State == "prepared") throw RecoveryRequired();
                RequireHash(Child(video, original.Backup), original.Hash);
                moved.Add(original.Name);
            }
            else RequireHash(Child(video, original.Name), original.Hash);
        }
        foreach (var output in transaction.Outputs)
        {
            var stageExists = Exists(Child(video, output.Stage));
            if (stageExists) RequireHash(Child(video, output.Stage), output.PartialHash ?? output.Hash);
            var original = transaction.Originals.SingleOrDefault(o => o.Name.Equals(output.Name, StringComparison.OrdinalIgnoreCase));
            var originalStillPresent = original is not null && !moved.Contains(original.Name);
            if (Exists(Child(video, output.Name)) && !originalStillPresent)
            {
                if (transaction.State != "publishing" || stageExists) throw RecoveryRequired();
                RequireHash(Child(video, output.Name), output.Hash);
                generated.Add(output);
            }
        }
        foreach (var original in transaction.Originals.Where(o => moved.Contains(o.Name)))
            if (Exists(Child(video, original.Name)) && !generated.Any(o => o.Name.Equals(original.Name, StringComparison.OrdinalIgnoreCase)))
                throw RecoveryRequired();

        foreach (var output in generated)
        {
            await ValidateVideoAsync(video, cancellationToken);
            DeleteOwned(Child(video, output.Name), output.Hash);
        }
        foreach (var original in transaction.Originals.Where(o => moved.Contains(o.Name)).Reverse())
        {
            await ValidateVideoAsync(video, cancellationToken);
            RequireHash(Child(video, original.Backup), original.Hash);
            EnsureAbsent(Child(video, original.Name));
            _files.Move(Child(video, original.Backup), Child(video, original.Name));
            _files.FlushDirectory(Path.GetDirectoryName(video.PhysicalPath)!);
        }
        foreach (var output in transaction.Outputs)
            if (Exists(Child(video, output.Stage))) DeleteOwned(Child(video, output.Stage), output.PartialHash ?? output.Hash);
        _files.FlushDirectory(Path.GetDirectoryName(video.PhysicalPath)!);
        DeleteJournal(journalPath);
    }

    private async Task CompleteAsync(Transaction transaction, string journalPath, CancellationToken cancellationToken)
    {
        var resolved = await ValidateVideoAsync(transaction.Video, cancellationToken);
        if (!PathsEqual(resolved.Source.RootPath, transaction.SourceRoot)) throw RecoveryRequired();
        foreach (var original in transaction.Originals) RequireHash(Child(transaction.Video, original.Backup), original.Hash);
        foreach (var output in transaction.Outputs)
        {
            if (Exists(Child(transaction.Video, output.Stage))) throw RecoveryRequired();
            RequireHash(Child(transaction.Video, output.Name), output.Hash);
        }
        await SaveAsync(ReceiptPath(transaction.Video), new Receipt(1, transaction.Video, transaction.SourceRoot,
            transaction.English, transaction.Romanian, transaction.Outputs.Select(o => new InstalledOutput(o.Name, o.Hash)).ToArray()), cancellationToken);
        DeleteJournal(journalPath);
    }

    private async Task<ResolvedSourcePath> ValidateVideoAsync(SubtitleVideo video, CancellationToken cancellationToken)
    {
        var root = await pathSecurity.ResolveAsync(video.SourceId, "/", cancellationToken);
        var resolved = await pathSecurity.ResolveAsync(video.SourceId, video.LogicalPath, cancellationToken);
        var directory = await pathSecurity.ResolveAsync(video.SourceId, video.DirectoryLogicalPath, cancellationToken);
        if (resolved.Source.IsReadOnly || root.Source.IsReadOnly || directory.Source.IsReadOnly)
            throw new SubtitleGenerationException("source_read_only", "The media source is read-only.");
        EnsureNoLinks(root.Source.RootPath, allowMissingLeaf: false);
        EnsureNoLinks(Path.Combine(root.Source.RootPath, video.LogicalPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)), false);
        EnsureNoLinks(resolved.PhysicalPath, false);
        if (!PathsEqual(video.PhysicalPath, resolved.PhysicalPath) || !PathsEqual(directory.PhysicalPath, Path.GetDirectoryName(video.PhysicalPath)!))
            throw new SubtitleGenerationException("subtitle_source_changed", "The video location has changed.");
        var info = new FileInfo(resolved.PhysicalPath);
        if (!info.Exists || info.Length != video.Length || info.LastWriteTimeUtc.Ticks != video.LastWriteTimeUtcTicks)
            throw new SubtitleGenerationException("subtitle_source_changed", "The video changed during subtitle generation.");
        return resolved;
    }

    private static (SrtDocument Document, byte[] Bytes) ValidateSrt(string? text)
    {
        try
        {
            if (text is null || text.Length > MaximumSubtitleBytes || StrictUtf8.GetByteCount(text) > MaximumSubtitleBytes) throw InvalidOutput();
            var document = SrtDocument.Parse(text, 100_000);
            long previousEnd = 0;
            foreach (var cue in document.Cues)
            {
                var lines = cue.Text.Split('\n');
                if (cue.StartMilliseconds < previousEnd || string.IsNullOrWhiteSpace(cue.Text) || lines.Length > 2 ||
                    lines.Any(line => string.IsNullOrWhiteSpace(line) || line.EnumerateRunes().Count() > 42 ||
                        line.Any(character => char.IsControl(character) || character == '\uFEFF'))) throw InvalidOutput();
                previousEnd = cue.EndMilliseconds;
            }
            return (document, document.RenderWithOffset(0));
        }
        catch (Exception exception) when (exception is MediaPreviewException or EncoderFallbackException or ArgumentException or OverflowException)
        {
            throw InvalidOutput();
        }
    }

    private void EnsureStorage()
    {
        foreach (var directory in new[] { paths.RootPath, Path.GetDirectoryName(_transactions)!, _transactions, _receipts })
        {
            EnsureNoLinks(directory, allowMissingLeaf: true);
            var existed = Directory.Exists(directory);
            Directory.CreateDirectory(directory);
            EnsureNoLinks(directory, allowMissingLeaf: false);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if (!existed && Path.GetDirectoryName(directory) is { } parent) _files.FlushDirectory(parent);
        }
    }

    private async Task SaveAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        EnsureNoLinks(path, allowMissingLeaf: true);
        await AtomicJsonFile.WriteAsync(path, value, cancellationToken);
        _files.FlushDirectory(Path.GetDirectoryName(path)!);
    }

    private static async Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        EnsureNoLinks(path, false);
        if (new FileInfo(path).Length > MaximumJournalBytes) throw RecoveryRequired();
        return await AtomicJsonFile.ReadAsync<T>(path, cancellationToken);
    }

    private void DeleteJournal(string path)
    {
        EnsureNoLinks(path, false);
        _files.Delete(path);
        _files.FlushDirectory(_transactions);
    }

    private void DeleteOwned(string path, string hash)
    {
        RequireHash(path, hash);
        _files.Delete(path);
        _files.FlushDirectory(Path.GetDirectoryName(path)!);
    }

    private static string Child(SubtitleVideo video, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(['/', '\\', ':', '\0']) >= 0 || Path.IsPathRooted(name))
            throw RecoveryRequired();
        var path = Path.Combine(Path.GetDirectoryName(video.PhysicalPath)!, name);
        if (PathsEqual(path, video.PhysicalPath)) throw RecoveryRequired();
        EnsureNoLinks(path, allowMissingLeaf: true);
        return path;
    }

    private static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static void EnsureAbsent(string path)
    {
        EnsureNoLinks(path, true);
        if (Exists(path)) throw new IOException("The subtitle destination is already occupied.");
        var name = Path.GetFileName(path);
        if (Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(path)!).Any(p => Path.GetFileName(p).Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("The subtitle destination has a case-insensitive collision.");
    }

    private static void EnsureNoLinks(string path, bool allowMissingLeaf)
    {
        var full = Path.GetFullPath(path);
        var current = Path.GetPathRoot(full)!;
        var components = full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < components.Length; index++)
        {
            current = Path.Combine(current, components[index]);
            try
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new SubtitleGenerationException(
                    "subtitle_unsafe_path", "Symbolic links are not supported for subtitle installation.");
                if (index < components.Length - 1 && (attributes & FileAttributes.Directory) == 0) throw RecoveryRequired();
            }
            catch (FileNotFoundException) when (allowMissingLeaf && index == components.Length - 1) { return; }
            catch (DirectoryNotFoundException) when (allowMissingLeaf && index == components.Length - 1) { return; }
        }
    }

    private void EnsureFreeSpace(string directory, long outputBytes)
    {
        var drive = new DriveInfo(directory);
        if (drive.AvailableFreeSpace < checked(Math.Max(0, options.Value.MinimumFreeBytes) + outputBytes))
            throw new SubtitleGenerationException("subtitle_insufficient_space", "There is not enough free space to install subtitles.");
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        EnsureNoLinks(path, false);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static async Task<bool> HasHashAsync(string path, string expected, CancellationToken cancellationToken) =>
        Exists(path) && string.Equals(await HashAsync(path, cancellationToken), expected, StringComparison.Ordinal);

    private static void RequireHash(string path, string expected)
    {
        EnsureNoLinks(path, false);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), expected, StringComparison.Ordinal)) throw RecoveryRequired();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static bool PathsEqual(string left, string right) => Path.GetFullPath(left).Equals(Path.GetFullPath(right), PathComparison);
    // Journal ownership follows output basenames, so changing a video extension cannot bypass pending recovery.
    private static string VideoKey(SubtitleVideo video) => Hash(StrictUtf8.GetBytes(video.SourceId.ToUpperInvariant() + "\n" +
        (OperatingSystem.IsWindows() ? video.DirectoryLogicalPath.ToUpperInvariant() : video.DirectoryLogicalPath) + "\n" +
        Path.GetFileNameWithoutExtension(video.LogicalPath).ToUpperInvariant()));
    private string ReceiptPath(SubtitleVideo video) => Path.Combine(_receipts, VideoKey(video) + ".json");
    private string TransactionPath(Transaction transaction) => Path.Combine(_transactions, $"{VideoKey(transaction.Video)}-{transaction.Id:N}.json");
    private string[] PendingTransactions(SubtitleVideo video) => Directory.GetFiles(_transactions, VideoKey(video) + "-*.json");

    private void ValidateTransaction(Transaction transaction, string path)
    {
        if (transaction.Version != 1 || transaction.Id == Guid.Empty || transaction.Video is null ||
            string.IsNullOrWhiteSpace(transaction.Video.SourceId) || string.IsNullOrWhiteSpace(transaction.Video.LogicalPath) ||
            string.IsNullOrWhiteSpace(transaction.Video.PhysicalPath) || string.IsNullOrWhiteSpace(transaction.Video.DirectoryLogicalPath) ||
            string.IsNullOrWhiteSpace(transaction.SourceRoot) || transaction.Originals is null || transaction.Outputs is null ||
            transaction.Originals.Any(o => o is null) || transaction.Outputs.Any(o => o is null) ||
            transaction.State is not ("prepared" or "publishing" or "committed") ||
            !PathsEqual(path, TransactionPath(transaction)) || transaction.Originals.Length > 10_000 ||
            !transaction.Outputs.Select(o => o.Name).SequenceEqual(SubtitleNaming.OutputNames(
                Path.GetFileName(transaction.Video.PhysicalPath), transaction.English, transaction.Romanian))) throw RecoveryRequired();
        foreach (var original in transaction.Originals)
        {
            _ = Child(transaction.Video, original.Name);
            _ = Child(transaction.Video, original.Backup);
            if (!original.Backup.EndsWith(".bak", StringComparison.Ordinal) || original.Hash is not { Length: 64 }) throw RecoveryRequired();
        }
        for (var index = 0; index < transaction.Outputs.Length; index++)
        {
            var output = transaction.Outputs[index];
            if (output.Stage != $".reachcommander-generated-subtitle-{transaction.Id:N}-{index}.partial" || output.Hash is not { Length: 64 } ||
                (output.PartialHash is not null && (transaction.State != "prepared" || output.PartialHash.Length != 64))) throw RecoveryRequired();
        }
    }

    private static bool IsRecoverable(Exception exception) => exception is IOException or UnauthorizedAccessException or NotSupportedException or
        OperationCanceledException or SubtitleGenerationException or JsonException or ArgumentException or InvalidOperationException or FileAccessException or SourceNotFoundException;
    private static SubtitleGenerationException InvalidOutput() => new("subtitle_output_invalid", "Generated subtitles are invalid or have unreadable lines.");
    private static SubtitleGenerationException RecoveryRequired() => new("subtitle_recovery_required", "An interrupted subtitle installation needs recovery. Existing files have been preserved.");

    private sealed record Original(string Name, string Backup, string Hash);
    private sealed record Output(string Name, string Stage, string Hash, string? PartialHash = null);
    private sealed record Transaction(int Version, Guid Id, SubtitleVideo Video, string SourceRoot, bool English, bool Romanian,
        string State, Original[] Originals, Output[] Outputs);
    private sealed record InstalledOutput(string Name, string Hash);
    private sealed record Receipt(int Version, SubtitleVideo Video, string SourceRoot, bool English, bool Romanian, InstalledOutput[] Outputs);
}

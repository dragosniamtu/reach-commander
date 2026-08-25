using ReachCommander.Domain.Files;

namespace ReachCommander.Infrastructure.FileOperations.Planning;

internal sealed record FileOperationEntryFingerprint(
    FileEntryType Type,
    long? Length,
    DateTimeOffset ModifiedAt,
    FileAttributes Attributes,
    bool IsSymbolicLink);

internal sealed record FileOperationEntrySnapshot(
    string SourceId,
    string LogicalPath,
    string Name,
    FileOperationEntryFingerprint Fingerprint)
{
    internal FileEntryType Type => Fingerprint.Type;

    internal long? Length => Fingerprint.Length;

    internal bool IsSymbolicLink => Fingerprint.IsSymbolicLink;
}

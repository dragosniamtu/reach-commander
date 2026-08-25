namespace ReachCommander.Infrastructure.FileOperations.Persistence;

internal sealed record FileOperationJournalEntry(
    string SourceId,
    string ParentLogicalPath,
    string OwnedName,
    string? PublicDestinationLogicalPath,
    bool IsQuarantine);

internal sealed record FileOperationExecutionJournal(
    Guid OperationId,
    IReadOnlyList<FileOperationJournalEntry> Entries);

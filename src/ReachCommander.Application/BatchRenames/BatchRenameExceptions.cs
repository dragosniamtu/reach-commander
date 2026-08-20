namespace ReachCommander.Application.BatchRenames;

public abstract class BatchRenameException(string message) : Exception(message);

public sealed class InvalidRenameRuleException(string message) : BatchRenameException(message);

public sealed class BatchTooLargeException(string message) : BatchRenameException(message);

public sealed class SourceReadOnlyException(string message) : BatchRenameException(message);

public sealed class RenamePlanNotFoundException(string message) : BatchRenameException(message);

public sealed class RenamePlanExpiredException(string message) : BatchRenameException(message);

public sealed class RenamePlanStaleException(string message) : BatchRenameException(message);

public sealed class RenameRecoveryRequiredException(string message) : BatchRenameException(message);

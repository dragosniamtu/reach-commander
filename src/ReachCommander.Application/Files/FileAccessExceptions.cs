namespace ReachCommander.Application.Files;

public abstract class FileAccessException(string message) : Exception(message);

public sealed class InvalidLogicalPathException(string logicalPath, string reason)
    : FileAccessException($"Logical path '{logicalPath}' is invalid: {reason}.")
{
    public string LogicalPath { get; } = logicalPath;
}

public sealed class PathConfinementException(string logicalPath)
    : FileAccessException($"Logical path '{logicalPath}' resolves outside its source.")
{
    public string LogicalPath { get; } = logicalPath;
}

public sealed class SourceUnavailableException(string sourceId)
    : FileAccessException($"Source '{sourceId}' is unavailable.")
{
    public string SourceId { get; } = sourceId;
}

public sealed class EntryNotFoundException(string sourceId, string logicalPath)
    : FileAccessException($"Entry '{logicalPath}' was not found in source '{sourceId}'.")
{
    public string SourceId { get; } = sourceId;

    public string LogicalPath { get; } = logicalPath;
}

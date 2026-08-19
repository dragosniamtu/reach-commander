namespace ReachCommander.Application.Sources;

public sealed class SourceConfigurationException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class SourceNotFoundException(string sourceId)
    : Exception($"Source '{sourceId}' was not found.")
{
    public string SourceId { get; } = sourceId;
}

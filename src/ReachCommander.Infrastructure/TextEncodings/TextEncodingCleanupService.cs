using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReachCommander.Application.Files;
using ReachCommander.Application.Sources;

namespace ReachCommander.Infrastructure.TextEncodings;

internal sealed class TextEncodingCleanupService(
    TextEncodingStagingRegistry registry,
    IPathSecurityService pathSecurity,
    ITextEncodingFileSystem fileSystem,
    TimeProvider clock,
    ILogger<TextEncodingCleanupService> logger) : IHostedService
{
    private static readonly TimeSpan StagingLifetime = TimeSpan.FromHours(24);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var manifest in registry.ReadAll())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = manifest.Record;
            if (!TextEncodingStagingRegistry.IsPrivateStagingName(record.StagingName))
            {
                registry.Quarantine(manifest.ManifestPath, "staging_name_invalid");
                continue;
            }

            if (record.CreatedAt > clock.GetUtcNow() - StagingLifetime)
            {
                continue;
            }

            try
            {
                var directory = await pathSecurity.ResolveAsync(
                    record.SourceId,
                    record.LogicalDirectory,
                    cancellationToken);
                if (!Directory.Exists(directory.PhysicalPath))
                {
                    throw new EntryNotFoundException(record.SourceId, record.LogicalDirectory);
                }

                var stagingPhysicalPath = Path.Combine(directory.PhysicalPath, record.StagingName);
                if (fileSystem.FileExists(stagingPhysicalPath))
                {
                    fileSystem.DeleteFile(stagingPhysicalPath);
                    fileSystem.FlushDirectory(directory.PhysicalPath);
                }

                registry.Remove(record.RecordId);
                logger.LogInformation(
                    "Cleaned text encoding staging record {RecordId} with result cleaned.",
                    record.RecordId);
            }
            catch (Exception exception) when (
                exception is SourceNotFoundException or SourceUnavailableException or EntryNotFoundException)
            {
                registry.Remove(record.RecordId);
                logger.LogInformation(
                    "Cleaned text encoding staging record {RecordId} with result already_missing.",
                    record.RecordId);
            }
            catch (Exception exception) when (
                exception is InvalidLogicalPathException or PathConfinementException)
            {
                registry.Quarantine(manifest.ManifestPath, "logical_path_invalid");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(
                    "Text encoding staging record {RecordId} could not be cleaned and will be retried.",
                    record.RecordId);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

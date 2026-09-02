using System.Text.Json;
using Microsoft.Extensions.Logging;
using ReachCommander.Infrastructure.Authentication;

namespace ReachCommander.Infrastructure.TextEncodings;

internal sealed record TextEncodingStagingRecord(
    Guid RecordId,
    string SourceId,
    string LogicalDirectory,
    string StagingName,
    DateTimeOffset CreatedAt);

internal sealed record TextEncodingStagingManifest(
    string ManifestPath,
    TextEncodingStagingRecord Record);

internal sealed class TextEncodingStagingRegistry(
    AuthenticationDataPaths dataPaths,
    TimeProvider clock,
    ILogger<TextEncodingStagingRegistry> logger)
{
    private const UnixFileMode OwnerDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode OwnerFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public string RegistryDirectory { get; } = Path.Combine(
        dataPaths.RootPath,
        "text-encodings",
        "staging");

    public async Task<TextEncodingStagingRecord> RegisterAsync(
        string sourceId,
        string logicalDirectory,
        string stagingName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalDirectory);
        if (!IsPrivateStagingName(stagingName))
        {
            throw new ArgumentException("The staging name is not private.", nameof(stagingName));
        }

        EnsureDirectory();
        var record = new TextEncodingStagingRecord(
            Guid.NewGuid(),
            sourceId,
            logicalDirectory,
            stagingName,
            clock.GetUtcNow());
        var manifestPath = GetManifestPath(record.RecordId);
        var temporaryPath = Path.Combine(
            RegistryDirectory,
            $".{record.RecordId:N}-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, record, cancellationToken: cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporaryPath, OwnerFileMode);
            }

            File.Move(temporaryPath, manifestPath, overwrite: false);
            return record;
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    public IReadOnlyList<TextEncodingStagingManifest> ReadAll()
    {
        EnsureDirectory();
        var manifests = new List<TextEncodingStagingManifest>();
        foreach (var manifestPath in Directory.EnumerateFiles(
                     RegistryDirectory,
                     "*.json",
                     SearchOption.TopDirectoryOnly))
        {
            try
            {
                var record = JsonSerializer.Deserialize<TextEncodingStagingRecord>(
                    File.ReadAllText(manifestPath));
                if (record is null ||
                    !Path.GetFileNameWithoutExtension(manifestPath).Equals(
                        record.RecordId.ToString("N"),
                        StringComparison.OrdinalIgnoreCase))
                {
                    Quarantine(manifestPath, "manifest_identity_invalid");
                    continue;
                }

                manifests.Add(new TextEncodingStagingManifest(manifestPath, record));
            }
            catch (Exception exception) when (
                exception is JsonException or IOException or UnauthorizedAccessException)
            {
                Quarantine(manifestPath, "manifest_unreadable");
            }
        }

        return manifests;
    }

    public string GetManifestPath(Guid recordId) =>
        Path.Combine(RegistryDirectory, $"{recordId:N}.json");

    public void Remove(Guid recordId) => TryDelete(GetManifestPath(recordId));

    public void Quarantine(string manifestPath, string resultCode)
    {
        var fullPath = Path.GetFullPath(manifestPath);
        if (!Path.GetDirectoryName(fullPath)!.Equals(
                Path.GetFullPath(RegistryDirectory),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            var destination = $"{fullPath}.invalid";
            if (File.Exists(destination))
            {
                destination = $"{destination}-{Guid.NewGuid():N}";
            }

            File.Move(fullPath, destination, overwrite: false);
            logger.LogWarning(
                "Quarantined text encoding staging manifest with result {ResultCode}.",
                resultCode);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(
                "Could not quarantine a text encoding staging manifest ({ResultCode}).",
                resultCode);
        }
    }

    internal static bool IsPrivateStagingName(string stagingName) =>
        !string.IsNullOrWhiteSpace(stagingName) &&
        stagingName.Equals(Path.GetFileName(stagingName), StringComparison.Ordinal) &&
        stagingName.StartsWith(
            ".reachcommander-operation-encoding-",
            StringComparison.Ordinal) &&
        stagingName.EndsWith(".partial", StringComparison.Ordinal) &&
        !stagingName.Contains('/') &&
        !stagingName.Contains('\\');

    private void EnsureDirectory()
    {
        Directory.CreateDirectory(RegistryDirectory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(RegistryDirectory, OwnerDirectoryMode);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}

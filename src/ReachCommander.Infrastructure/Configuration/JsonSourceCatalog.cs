using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReachCommander.Application.Sources;
using ReachCommander.Domain.Sources;

namespace ReachCommander.Infrastructure.Configuration;

public sealed partial class JsonSourceCatalog(
    IOptions<ReachCommanderOptions> options,
    ILogger<JsonSourceCatalog> logger) : ISourceCatalog
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private IReadOnlyList<SourceDefinition>? _definitions;

    public async ValueTask<IReadOnlyList<SourceDefinition>> GetDefinitionsAsync(
        CancellationToken cancellationToken)
    {
        if (_definitions is not null)
        {
            return _definitions;
        }

        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            if (_definitions is null)
            {
                _definitions = await LoadAsync(options.Value.SourcesPath, cancellationToken);
            }

            return _definitions;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public async ValueTask<IReadOnlyList<SourceSnapshot>> GetSnapshotsAsync(
        CancellationToken cancellationToken)
    {
        var definitions = await GetDefinitionsAsync(cancellationToken);
        var snapshots = new SourceSnapshot[definitions.Count];

        for (var index = 0; index < definitions.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            snapshots[index] = CreateSnapshot(definitions[index]);
        }

        return Array.AsReadOnly(snapshots);
    }

    public async ValueTask<SourceDefinition> GetRequiredAsync(
        string sourceId,
        CancellationToken cancellationToken)
    {
        var definitions = await GetDefinitionsAsync(cancellationToken);
        var source = definitions.FirstOrDefault(
            candidate => string.Equals(candidate.Id, sourceId, StringComparison.OrdinalIgnoreCase));

        return source ?? throw new SourceNotFoundException(sourceId);
    }

    private async Task<IReadOnlyList<SourceDefinition>> LoadAsync(
        string sourcesPath,
        CancellationToken cancellationToken)
    {
        SourcesFile file;
        try
        {
            await using var stream = File.OpenRead(sourcesPath);
            file = await JsonSerializer.DeserializeAsync<SourcesFile>(
                stream,
                SerializerOptions,
                cancellationToken) ?? throw new SourceConfigurationException("Source configuration is empty.");
        }
        catch (SourceConfigurationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new SourceConfigurationException(
                "Source configuration could not be loaded or parsed.",
                exception);
        }

        Validate(file.Sources);

        var enabled = file.Sources
            .Where(source => source.Enabled)
            .Select(source => new SourceDefinition(
                source.Id,
                source.Name.Trim(),
                Path.GetFullPath(source.Path),
                source.ReadOnly,
                source.DefaultLeft,
                source.DefaultRight))
            .ToArray();

        logger.LogInformation("Loaded {SourceCount} enabled filesystem sources", enabled.Length);
        return new ReadOnlyCollection<SourceDefinition>(enabled);
    }

    private static void Validate(IReadOnlyCollection<SourceFileEntry> sources)
    {
        if (sources.Count == 0 || sources.All(source => !source.Enabled))
        {
            throw new SourceConfigurationException("At least one enabled source is required.");
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            if (!ids.Add(source.Id))
            {
                throw new SourceConfigurationException($"Duplicate source ID '{source.Id}'.");
            }

            if (!SourceIdPattern().IsMatch(source.Id))
            {
                throw new SourceConfigurationException(
                    $"Source '{source.Id}' has an invalid source ID. Use lowercase letters, digits, hyphens, and underscores.");
            }

            if (string.IsNullOrWhiteSpace(source.Name))
            {
                throw new SourceConfigurationException($"Source '{source.Id}' must have a non-empty name.");
            }

            if (!Path.IsPathFullyQualified(source.Path))
            {
                throw new SourceConfigurationException($"Source '{source.Id}' path must be absolute.");
            }
        }

        var enabled = sources.Where(source => source.Enabled).ToArray();
        if (enabled.Count(source => source.DefaultLeft) > 1 ||
            enabled.Count(source => source.DefaultRight) > 1)
        {
            throw new SourceConfigurationException("Only one enabled source may be the default for each panel.");
        }
    }

    private static SourceSnapshot CreateSnapshot(SourceDefinition source)
    {
        if (!Directory.Exists(source.RootPath))
        {
            return Unavailable(source);
        }

        try
        {
            var root = Path.GetPathRoot(source.RootPath);
            if (string.IsNullOrEmpty(root))
            {
                return Unavailable(source);
            }

            var drive = new DriveInfo(root);
            var total = drive.TotalSize;
            var free = drive.AvailableFreeSpace;
            return new SourceSnapshot(
                source.Id,
                source.Name,
                IsAvailable: true,
                source.IsReadOnly,
                total,
                total - free,
                free,
                source.DefaultLeft,
                source.DefaultRight);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new SourceSnapshot(
                source.Id,
                source.Name,
                IsAvailable: true,
                source.IsReadOnly,
                TotalBytes: null,
                UsedBytes: null,
                FreeBytes: null,
                source.DefaultLeft,
                source.DefaultRight);
        }
    }

    private static SourceSnapshot Unavailable(SourceDefinition source) => new(
        source.Id,
        source.Name,
        IsAvailable: false,
        source.IsReadOnly,
        TotalBytes: null,
        UsedBytes: null,
        FreeBytes: null,
        source.DefaultLeft,
        source.DefaultRight);

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9_-]*[a-z0-9])?$")]
    private static partial Regex SourceIdPattern();
}

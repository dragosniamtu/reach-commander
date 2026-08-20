using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using ReachCommander.Application.Archives;
using ReachCommander.Application.Files;
using ReachCommander.Application.Sources;
using ReachCommander.Domain.Archives;
using ReachCommander.Infrastructure.Archives.Catalog;

namespace ReachCommander.Infrastructure.Archives.Extraction;

internal sealed record ArchiveDestinationEntry(
    string Name,
    bool IsDirectory,
    long? Length,
    DateTimeOffset LastWriteTimeUtc);

internal interface IArchiveExtractionFileSystem
{
    bool DirectoryExists(string physicalPath);

    IReadOnlyList<ArchiveDestinationEntry> ListChildren(string physicalDirectory);

    long? GetAvailableFreeSpace(string physicalDirectory);
}

internal sealed class LocalArchiveExtractionFileSystem : IArchiveExtractionFileSystem
{
    public bool DirectoryExists(string physicalPath) => Directory.Exists(physicalPath);

    public IReadOnlyList<ArchiveDestinationEntry> ListChildren(string physicalDirectory) =>
        Array.AsReadOnly(new DirectoryInfo(physicalDirectory)
            .EnumerateFileSystemInfos()
            .Select(entry => new ArchiveDestinationEntry(
                entry.Name,
                entry is DirectoryInfo,
                entry is FileInfo file ? file.Length : null,
                new DateTimeOffset(entry.LastWriteTimeUtc)))
            .ToArray());

    public long? GetAvailableFreeSpace(string physicalDirectory)
    {
        var fullPath = Path.GetFullPath(physicalDirectory);
        var drives = DriveInfo.GetDrives();
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var root = FindContainingVolumeRoot(
            fullPath,
            drives.Select(drive => drive.RootDirectory.FullName),
            comparison);
        if (root is null)
        {
            return null;
        }

        return drives.First(drive => NormalizeVolumeRoot(drive.RootDirectory.FullName)
                .Equals(root, comparison))
            .AvailableFreeSpace;
    }

    internal static string? FindContainingVolumeRoot(
        string physicalDirectory,
        IEnumerable<string> volumeRoots,
        StringComparison comparison)
    {
        var candidate = NormalizeVolumeRoot(physicalDirectory);
        return volumeRoots
            .Select(NormalizeVolumeRoot)
            .Where(root => IsWithinVolume(candidate, root, comparison))
            .OrderByDescending(root => root.Length)
            .FirstOrDefault();
    }

    private static bool IsWithinVolume(
        string candidate,
        string root,
        StringComparison comparison)
    {
        if (candidate.Equals(root, comparison))
        {
            return true;
        }

        if (root.EndsWith('/') || root.EndsWith('\\'))
        {
            return candidate.StartsWith(root, comparison);
        }

        return candidate.StartsWith(root, comparison) &&
            candidate.Length > root.Length &&
            candidate[root.Length] is '/' or '\\';
    }

    private static string NormalizeVolumeRoot(string value)
    {
        var trimmed = value.TrimEnd('/', '\\');
        if (trimmed.Length == 0)
        {
            return value.Contains('\\') ? "\\" : "/";
        }

        if (trimmed.Length == 2 && trimmed[1] == ':')
        {
            return $"{trimmed}\\";
        }

        return trimmed;
    }
}

internal interface IArchivePlanIdGenerator
{
    string CreateId();
}

internal sealed class ArchivePlanIdGenerator : IArchivePlanIdGenerator
{
    public string CreateId()
    {
        Span<byte> value = stackalloc byte[32];
        RandomNumberGenerator.Fill(value);
        return Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}

internal sealed class ArchiveExtractionPlanner(
    IArchiveCatalogProvider catalogProvider,
    ISourceCatalog sourceCatalog,
    IPathSecurityService pathSecurity,
    IArchiveExtractionFileSystem fileSystem,
    ArchiveExtractionPlanStore planStore,
    IArchivePlanIdGenerator idGenerator,
    IOptions<ArchiveOptions> options,
    TimeProvider clock)
{
    private const int MaximumIssues = 100;
    private const int MaximumIssuePaths = 100;
    private const int MaximumSafeTextCharacters = 512;
    private readonly ArchiveOptions _options = options.Value;

    public async ValueTask<ArchiveExtractionPreview> PreviewAsync(
        ArchiveExtractionPreviewRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var resolvedCatalog = await catalogProvider.GetAsync(
            request.SourceId,
            request.ArchivePath,
            cancellationToken);
        var destination = await ResolveDestinationAsync(request, cancellationToken);
        var destinationEntries = ReadDestinationEntries(destination.PhysicalPath);
        var destinationSnapshot = CreateDestinationSnapshot(destinationEntries);
        var internalDirectory = NormalizeArchivePath(request.InternalDirectory);

        var conflicts = new List<ArchiveExtractionIssue>();
        var violations = new List<ArchiveExtractionIssue>();
        IReadOnlyList<ArchiveCatalogNode> expanded;
        try
        {
            expanded = resolvedCatalog.Catalog.ExpandSelection(
                internalDirectory,
                request.EntryPaths,
                request.ExtractAll);
        }
        catch (ArchiveEntryUnsafeException)
        {
            expanded = [];
            violations.Add(Issue(
                "archive_entry_unsafe",
                "Choose valid entries from the current archive directory.",
                request.EntryPaths));
        }

        var roots = FindRoots(expanded);
        var selectedRoots = roots
            .Select(root => MakeRelative(root.Path, internalDirectory))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var files = expanded
            .Where(node => node.Type == ArchiveEntryType.File)
            .Select(node => ToPlannedFile(node, internalDirectory))
            .OrderBy(file => file.RelativeOutputPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var directories = expanded
            .Where(node => node.Type == ArchiveEntryType.Directory)
            .Select(node => MakeRelative(node.Path, internalDirectory))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        ValidateSelectedLimits(destination.LogicalPath, files, directories, violations);
        await ValidateOutputsAsync(
            destination,
            directories.Concat(files.Select(file => file.RelativeOutputPath)),
            violations,
            cancellationToken);
        AddConflicts(selectedRoots, destinationEntries, conflicts);

        var totalSize = SumKnownSizes(files);
        var freeSpace = ReadAvailableFreeSpace(destination.PhysicalPath);
        if (totalSize is not null && freeSpace is not null && totalSize > freeSpace)
        {
            violations.Add(Issue(
                "archive_limit_exceeded",
                "The selected entries exceed the destination's available free space.",
                selectedRoots));
        }

        conflicts = conflicts.Take(MaximumIssues).ToList();
        violations = violations.Take(MaximumIssues).ToList();
        var canExecute = expanded.Count > 0 && conflicts.Count == 0 && violations.Count == 0;
        var now = clock.GetUtcNow();
        var planId = idGenerator.CreateId();
        var expiresAt = now.Add(_options.PlanLifetime);
        var immutableConflicts = Array.AsReadOnly(conflicts.ToArray());
        var immutableViolations = Array.AsReadOnly(violations.ToArray());
        var immutableRoots = Array.AsReadOnly(selectedRoots);
        var immutableFiles = Array.AsReadOnly(files);
        var immutableDirectories = Array.AsReadOnly(directories);
        var plan = new ArchiveExtractionPlan(
            planId,
            now,
            expiresAt,
            request.SourceId,
            resolvedCatalog.PartSet.PrimaryLogicalPath,
            resolvedCatalog.PartSet,
            internalDirectory,
            immutableRoots,
            immutableFiles,
            immutableDirectories,
            destination.Source.Id,
            destination.LogicalPath,
            destinationSnapshot,
            immutableConflicts,
            immutableViolations,
            canExecute);
        planStore.Add(plan);

        return new ArchiveExtractionPreview(
            planId,
            expiresAt,
            resolvedCatalog.Catalog.Format,
            resolvedCatalog.PartSet.Parts.Count,
            immutableRoots,
            files.Length,
            directories.Length,
            totalSize,
            destination.Source.Id,
            destination.LogicalPath,
            immutableConflicts,
            immutableViolations,
            canExecute);
    }

    private async ValueTask<ResolvedSourcePath> ResolveDestinationAsync(
        ArchiveExtractionPreviewRequest request,
        CancellationToken cancellationToken)
    {
        var snapshots = await sourceCatalog.GetSnapshotsAsync(cancellationToken);
        var snapshot = snapshots.FirstOrDefault(source =>
            source.Id.Equals(request.DestinationSourceId, StringComparison.OrdinalIgnoreCase));
        if (snapshot is null || !snapshot.IsAvailable)
        {
            throw new ArchiveDestinationInvalidException();
        }

        if (snapshot.IsReadOnly)
        {
            throw new ArchiveDestinationReadOnlyException(snapshot.Id);
        }

        ResolvedSourcePath destination;
        try
        {
            destination = await pathSecurity.ResolveAsync(
                snapshot.Id,
                request.DestinationPath,
                cancellationToken);
        }
        catch (FileAccessException)
        {
            throw new ArchiveDestinationInvalidException();
        }

        if (destination.Source.IsReadOnly)
        {
            throw new ArchiveDestinationReadOnlyException(destination.Source.Id);
        }

        if (!fileSystem.DirectoryExists(destination.PhysicalPath))
        {
            throw new ArchiveDestinationInvalidException();
        }

        return destination;
    }

    private IReadOnlyList<ArchiveDestinationEntry> ReadDestinationEntries(string physicalPath)
    {
        try
        {
            return fileSystem.ListChildren(physicalPath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new ArchiveDestinationInvalidException();
        }
    }

    private long? ReadAvailableFreeSpace(string physicalPath)
    {
        try
        {
            return fileSystem.GetAvailableFreeSpace(physicalPath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new ArchiveDestinationInvalidException();
        }
    }

    private async ValueTask ValidateOutputsAsync(
        ResolvedSourcePath destination,
        IEnumerable<string> relativePaths,
        ICollection<ArchiveExtractionIssue> violations,
        CancellationToken cancellationToken)
    {
        foreach (var relativePath in relativePaths
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                _ = await pathSecurity.ResolveDescendantAsync(
                    destination.Source.Id,
                    destination.LogicalPath,
                    relativePath,
                    cancellationToken);
            }
            catch (FileAccessException)
            {
                violations.Add(Issue(
                    "archive_entry_unsafe",
                    "An extraction output name is not valid for the destination.",
                    [relativePath]));
            }
        }
    }

    private void ValidateSelectedLimits(
        string destinationPath,
        IReadOnlyList<PlannedArchiveFile> files,
        IReadOnlyList<string> directories,
        ICollection<ArchiveExtractionIssue> violations)
    {
        if (files.Count + directories.Count > _options.MaxEntries)
        {
            violations.Add(Limit("The selection exceeds the configured entry-count limit."));
        }

        foreach (var file in files)
        {
            if (file.DeclaredSize > _options.MaxSingleExtractedFileBytes)
            {
                violations.Add(Limit(
                    "A selected file exceeds the configured single-file size limit.",
                    [file.RelativeOutputPath]));
            }

            if (file.DeclaredSize is > 0 && file.DeclaredCompressedSize is not null &&
                (file.DeclaredCompressedSize == 0 ||
                 file.DeclaredSize.Value / (double)file.DeclaredCompressedSize.Value >
                 _options.MaxExpansionRatio))
            {
                violations.Add(Limit(
                    "A selected file exceeds the configured expansion-ratio limit.",
                    [file.RelativeOutputPath]));
            }
        }

        foreach (var relativePath in files.Select(file => file.RelativeOutputPath).Concat(directories))
        {
            var finalPath = JoinLogicalPath(destinationPath, relativePath);
            var components = finalPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (components.Length > _options.MaxPathDepth ||
                components.Any(component => component.Length > _options.MaxComponentCharacters) ||
                finalPath.Length > _options.MaxPathCharacters)
            {
                violations.Add(Limit(
                    "An extraction output path exceeds the configured path limit.",
                    [relativePath]));
            }
        }

        var total = SumKnownSizes(files);
        if (total > _options.MaxTotalExtractedBytes)
        {
            violations.Add(Limit("The selection exceeds the configured extracted-size limit."));
        }
    }

    private static PlannedArchiveFile ToPlannedFile(
        ArchiveCatalogNode node,
        string internalDirectory)
    {
        if (node.WorkerEntryIndex is null)
        {
            throw new ArchiveEntryUnsafeException();
        }

        return new PlannedArchiveFile(
            node.WorkerEntryIndex.Value,
            node.Path,
            MakeRelative(node.Path, internalDirectory),
            node.Size,
            node.CompressedSize,
            node.ModifiedAt);
    }

    private static IReadOnlyList<ArchiveCatalogNode> FindRoots(
        IReadOnlyList<ArchiveCatalogNode> expanded)
    {
        var paths = expanded.Select(node => node.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Array.AsReadOnly(expanded
            .Where(node => !HasSelectedAncestor(node.Path, paths))
            .OrderBy(node => node.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray());
    }

    private static bool HasSelectedAncestor(string path, IReadOnlySet<string> selected)
    {
        var separator = path.LastIndexOf('/');
        while (separator > 0)
        {
            path = path[..separator];
            if (selected.Contains(path))
            {
                return true;
            }

            separator = path.LastIndexOf('/');
        }

        return false;
    }

    private static void AddConflicts(
        IReadOnlyList<string> selectedRoots,
        IReadOnlyList<ArchiveDestinationEntry> destinationEntries,
        ICollection<ArchiveExtractionIssue> conflicts)
    {
        var existing = destinationEntries
            .Select(entry => entry.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var occupied = selectedRoots
            .Where(root => existing.Contains(TopLevelComponent(root)))
            .ToArray();
        var colliding = selectedRoots
            .GroupBy(TopLevelComponent, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .ToArray();
        var paths = occupied.Concat(colliding)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length > 0)
        {
            conflicts.Add(Issue(
                "archive_destination_conflict",
                "One or more extraction destination names already exist or collide.",
                paths));
        }
    }

    private static long? SumKnownSizes(IReadOnlyList<PlannedArchiveFile> files)
    {
        long total = 0;
        foreach (var file in files)
        {
            if (file.DeclaredSize is null)
            {
                return null;
            }

            try
            {
                total = checked(total + file.DeclaredSize.Value);
            }
            catch (OverflowException)
            {
                return long.MaxValue;
            }
        }

        return total;
    }

    internal static string CreateDestinationSnapshot(
        IReadOnlyList<ArchiveDestinationEntry> entries)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var entry in entries.OrderBy(
                         entry => entry.Name,
                         StringComparer.OrdinalIgnoreCase))
            {
                writer.Write(entry.Name.Normalize(NormalizationForm.FormC).ToUpperInvariant());
                writer.Write(entry.IsDirectory);
                writer.Write(entry.Length ?? -1);
                writer.Write(entry.LastWriteTimeUtc.UtcTicks);
            }
        }

        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length)));
    }

    private static ArchiveExtractionIssue Limit(
        string message,
        IReadOnlyList<string>? paths = null) =>
        Issue("archive_limit_exceeded", message, paths ?? []);

    private static ArchiveExtractionIssue Issue(
        string code,
        string message,
        IEnumerable<string> paths) =>
        new(
            SafeText(code),
            SafeText(message),
            Array.AsReadOnly(paths
                .Take(MaximumIssuePaths)
                .Select(SafeText)
                .ToArray()));

    private static string SafeText(string value) =>
        new(value.Where(character => !char.IsControl(character)).Take(MaximumSafeTextCharacters).ToArray());

    private static string NormalizeArchivePath(string value)
    {
        if (value == "/")
        {
            return value;
        }

        if (string.IsNullOrEmpty(value) ||
            !value.StartsWith("/", StringComparison.Ordinal) ||
            value.EndsWith("/", StringComparison.Ordinal) ||
            value.Contains('\\') ||
            value.Contains("//", StringComparison.Ordinal))
        {
            throw new ArchiveEntryUnsafeException();
        }

        var components = value[1..].Split('/', StringSplitOptions.None);
        if (components.Any(component => component.Length == 0 || component is "." or ".."))
        {
            throw new ArchiveEntryUnsafeException();
        }

        return $"/{string.Join('/', components.Select(component =>
            component.Normalize(NormalizationForm.FormC)))}";
    }

    private static string MakeRelative(string archivePath, string internalDirectory)
    {
        if (internalDirectory == "/")
        {
            return archivePath[1..];
        }

        var prefix = $"{internalDirectory}/";
        if (!archivePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArchiveEntryUnsafeException();
        }

        return archivePath[prefix.Length..];
    }

    private static string TopLevelComponent(string relativePath)
    {
        var separator = relativePath.IndexOf('/');
        return separator < 0 ? relativePath : relativePath[..separator];
    }

    private static string JoinLogicalPath(string destination, string relativePath) =>
        destination == "/" ? $"/{relativePath}" : $"{destination}/{relativePath}";
}

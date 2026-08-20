using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using ReachCommander.Application.Archives;
using ReachCommander.Application.Files;
using ReachCommander.Domain.Archives;
using ReachCommander.Infrastructure.Archives.Classification;

namespace ReachCommander.Infrastructure.Archives.Volumes;

internal sealed record ResolvedArchivePart(
    string LogicalPath,
    string PhysicalPath,
    long Length,
    DateTimeOffset LastWriteTimeUtc);

internal sealed record ResolvedArchivePartSet(
    ArchiveFormat Format,
    string PrimaryLogicalPath,
    IReadOnlyList<ResolvedArchivePart> Parts,
    ArchiveVolumeFingerprint Fingerprint);

internal interface IArchivePartResolver
{
    ValueTask<ResolvedArchivePartSet> ResolveAsync(
        string sourceId,
        string archivePath,
        CancellationToken cancellationToken);
}

internal sealed partial class ArchivePartResolver(
    IPathSecurityService pathSecurity,
    IOptions<ArchiveOptions> options) : IArchivePartResolver
{
    private readonly ArchiveOptions _options = options.Value;

    public async ValueTask<ResolvedArchivePartSet> ResolveAsync(
        string sourceId,
        string archivePath,
        CancellationToken cancellationToken)
    {
        var (parentLogicalPath, requestedName) = SplitLogicalPath(archivePath);
        var parent = await pathSecurity.ResolveAsync(
            sourceId,
            parentLogicalPath,
            cancellationToken);
        var requested = await ResolveRegularPartAsync(
            sourceId,
            parent,
            requestedName,
            failureNames: [],
            cancellationToken);

        var fileSystemEntries = new DirectoryInfo(parent.PhysicalPath)
            .EnumerateFileSystemInfos()
            .ToArray();
        var siblingNames = fileSystemEntries
            .Select(entry => entry.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var classification = ArchiveFilenameClassifier.Classify(
            requestedName,
            isLink: false,
            siblingNames) ?? throw new ArchiveUnsupportedException();
        var requestedVolumeName = ParseVolumeName(requestedName, classification.Format);

        if (classification.Role == ArchiveRole.Secondary)
        {
            var primaryName = GetPrimaryName(requestedVolumeName);
            throw new ArchiveVolumeSecondaryException(
                JoinLogicalPath(parent.LogicalPath, primaryName));
        }

        var orderedNames = ResolveOrderedNames(
            requestedVolumeName,
            fileSystemEntries.Select(entry => entry.Name).ToArray(),
            parent.LogicalPath);
        if (orderedNames.Count > _options.MaxVolumes)
        {
            throw new ArchiveLimitExceededException(
                "The archive exceeds the configured volume-count limit.");
        }

        var parts = new List<ResolvedArchivePart>(orderedNames.Count);
        long totalBytes = 0;
        foreach (var name in orderedNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var logicalName = JoinLogicalPath(parent.LogicalPath, name);
            var part = name.Equals(requestedName, StringComparison.OrdinalIgnoreCase)
                ? requested
                : await ResolveRegularPartAsync(
                    sourceId,
                    parent,
                    name,
                    [logicalName],
                    cancellationToken);

            try
            {
                totalBytes = checked(totalBytes + part.Length);
            }
            catch (OverflowException)
            {
                throw new ArchiveLimitExceededException(
                    "The archive compressed-size total exceeds the configured limit.");
            }

            if (totalBytes > _options.MaxTotalCompressedBytes)
            {
                throw new ArchiveLimitExceededException(
                    "The archive compressed-size total exceeds the configured limit.");
            }

            parts.Add(part);
        }

        EnsureUniquePhysicalPaths(parts);
        var primaryLogicalPath = JoinLogicalPath(parent.LogicalPath, orderedNames[0]);
        if (requestedVolumeName.Kind == ArchiveVolumeKind.ClassicZip)
        {
            primaryLogicalPath = JoinLogicalPath(parent.LogicalPath, requestedName);
        }

        var readOnlyParts = parts.AsReadOnly();
        return new ResolvedArchivePartSet(
            classification.Format,
            primaryLogicalPath,
            readOnlyParts,
            ArchiveVolumeFingerprint.Create(sourceId, primaryLogicalPath, readOnlyParts));
    }

    private async ValueTask<ResolvedArchivePart> ResolveRegularPartAsync(
        string sourceId,
        ResolvedSourcePath parent,
        string name,
        IReadOnlyList<string> failureNames,
        CancellationToken cancellationToken)
    {
        ResolvedSourcePath resolved;
        try
        {
            resolved = await pathSecurity.ResolveChildAsync(
                sourceId,
                parent.LogicalPath,
                name,
                cancellationToken);
        }
        catch (ArchiveException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidLogicalPathException or
                EntryNotFoundException or
                SourceUnavailableException or
                UnauthorizedAccessException or
                IOException)
        {
            throw new ArchiveVolumeSetInvalidException(failureNames);
        }

        var file = new FileInfo(resolved.PhysicalPath);
        file.Refresh();
        if (!file.Exists ||
            Directory.Exists(resolved.PhysicalPath) ||
            file.LinkTarget is not null ||
            file.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new ArchiveVolumeSetInvalidException(failureNames);
        }

        return new ResolvedArchivePart(
            resolved.LogicalPath,
            Path.GetFullPath(resolved.PhysicalPath),
            file.Length,
            new DateTimeOffset(file.LastWriteTimeUtc));
    }

    private static IReadOnlyList<string> ResolveOrderedNames(
        ArchiveVolumeName requested,
        IReadOnlyList<string> siblingNames,
        string parentLogicalPath)
    {
        var related = siblingNames
            .Select(TryParseVolumeName)
            .Where(candidate =>
                candidate is not null &&
                candidate.Format == requested.Format &&
                candidate.Stem.Equals(requested.Stem, StringComparison.OrdinalIgnoreCase))
            .Cast<ArchiveVolumeName>()
            .ToArray();

        return requested.Kind switch
        {
            ArchiveVolumeKind.SingleRar => ResolveTerminalPrimary(
                requested,
                related,
                ArchiveVolumeKind.LegacyRar,
                startIndex: 0,
                terminalLast: false,
                parentLogicalPath),
            ArchiveVolumeKind.SingleZip => ResolveTerminalPrimary(
                requested,
                related,
                ArchiveVolumeKind.ClassicZip,
                startIndex: 1,
                terminalLast: true,
                parentLogicalPath),
            ArchiveVolumeKind.SingleSevenZip => ResolveSingle(requested, related),
            ArchiveVolumeKind.ModernRar => ResolveNumbered(
                requested,
                related,
                ArchiveVolumeKind.ModernRar,
                startIndex: 1,
                parentLogicalPath),
            ArchiveVolumeKind.SplitSevenZip => ResolveNumbered(
                requested,
                related,
                ArchiveVolumeKind.SplitSevenZip,
                startIndex: 1,
                parentLogicalPath),
            ArchiveVolumeKind.SplitZip => ResolveNumbered(
                requested,
                related,
                ArchiveVolumeKind.SplitZip,
                startIndex: 1,
                parentLogicalPath),
            _ => throw new ArchiveVolumeSetInvalidException([]),
        };
    }

    private static IReadOnlyList<string> ResolveTerminalPrimary(
        ArchiveVolumeName requested,
        IReadOnlyList<ArchiveVolumeName> related,
        ArchiveVolumeKind numberedKind,
        int startIndex,
        bool terminalLast,
        string parentLogicalPath)
    {
        var numbered = related.Where(candidate => candidate.Kind == numberedKind).ToArray();
        var incompatible = related.Any(candidate =>
            candidate.Kind != requested.Kind && candidate.Kind != numberedKind);
        if (incompatible)
        {
            throw new ArchiveVolumeSetInvalidException([]);
        }

        if (numbered.Length == 0)
        {
            return [requested.Name];
        }

        var ordered = ValidateAndOrderNumbered(numbered, startIndex, parentLogicalPath);
        return terminalLast
            ? [.. ordered, requested.Name]
            : [requested.Name, .. ordered];
    }

    private static IReadOnlyList<string> ResolveSingle(
        ArchiveVolumeName requested,
        IReadOnlyList<ArchiveVolumeName> related)
    {
        if (related.Any(candidate => candidate.Kind != requested.Kind))
        {
            throw new ArchiveVolumeSetInvalidException([]);
        }

        return [requested.Name];
    }

    private static IReadOnlyList<string> ResolveNumbered(
        ArchiveVolumeName requested,
        IReadOnlyList<ArchiveVolumeName> related,
        ArchiveVolumeKind expectedKind,
        int startIndex,
        string parentLogicalPath)
    {
        if (related.Any(candidate => candidate.Kind != expectedKind))
        {
            throw new ArchiveVolumeSetInvalidException([]);
        }

        return ValidateAndOrderNumbered(
            related.Where(candidate => candidate.Kind == expectedKind).ToArray(),
            startIndex,
            parentLogicalPath);
    }

    private static IReadOnlyList<string> ValidateAndOrderNumbered(
        IReadOnlyList<ArchiveVolumeName> candidates,
        int startIndex,
        string parentLogicalPath)
    {
        var duplicate = candidates
            .GroupBy(candidate => candidate.Index)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArchiveVolumeSetInvalidException([]);
        }

        if (candidates.Count == 0)
        {
            throw new ArchiveVolumeSetInvalidException([]);
        }

        var byIndex = candidates.ToDictionary(candidate => candidate.Index!.Value);
        var maximum = byIndex.Keys.Max();
        var missing = new List<string>();
        for (var index = startIndex; index <= maximum; index++)
        {
            if (!byIndex.ContainsKey(index))
            {
                missing.Add(JoinLogicalPath(
                    parentLogicalPath,
                    FormatIndexedName(candidates[0], index)));
            }
        }

        if (missing.Count > 0)
        {
            throw new ArchiveVolumeSetInvalidException(missing);
        }

        return byIndex.OrderBy(pair => pair.Key).Select(pair => pair.Value.Name).ToArray();
    }

    private static string FormatIndexedName(ArchiveVolumeName template, int index) =>
        template.Kind switch
        {
            ArchiveVolumeKind.ModernRar =>
                $"{template.Stem}.part{index.ToString(new string('0', template.IndexWidth), CultureInfo.InvariantCulture)}.rar",
            ArchiveVolumeKind.LegacyRar =>
                $"{template.Stem}.r{index.ToString(new string('0', template.IndexWidth), CultureInfo.InvariantCulture)}",
            ArchiveVolumeKind.SplitSevenZip =>
                $"{template.Stem}.7z.{index.ToString(new string('0', template.IndexWidth), CultureInfo.InvariantCulture)}",
            ArchiveVolumeKind.SplitZip =>
                $"{template.Stem}.zip.{index.ToString(new string('0', template.IndexWidth), CultureInfo.InvariantCulture)}",
            ArchiveVolumeKind.ClassicZip =>
                $"{template.Stem}.z{index.ToString(new string('0', template.IndexWidth), CultureInfo.InvariantCulture)}",
            _ => template.Name,
        };

    private static ArchiveVolumeName ParseVolumeName(string name, ArchiveFormat? expectedFormat)
    {
        var parsed = TryParseVolumeName(name);
        if (parsed is null || (expectedFormat is not null && parsed.Format != expectedFormat))
        {
            throw new ArchiveUnsupportedException();
        }

        return parsed;
    }

    private static ArchiveVolumeName? TryParseVolumeName(string name)
    {
        var match = ModernRarRegex().Match(name);
        if (TryCreateIndexed(match, name, ArchiveFormat.Rar, ArchiveVolumeKind.ModernRar, 1, out var result))
        {
            return result;
        }

        match = LegacyRarRegex().Match(name);
        if (TryCreateIndexed(match, name, ArchiveFormat.Rar, ArchiveVolumeKind.LegacyRar, 0, out result))
        {
            return result;
        }

        match = SplitSevenZipRegex().Match(name);
        if (TryCreateIndexed(match, name, ArchiveFormat.SevenZip, ArchiveVolumeKind.SplitSevenZip, 1, out result))
        {
            return result;
        }

        match = SplitZipRegex().Match(name);
        if (TryCreateIndexed(match, name, ArchiveFormat.Zip, ArchiveVolumeKind.SplitZip, 1, out result))
        {
            return result;
        }

        match = ClassicZipRegex().Match(name);
        if (TryCreateIndexed(match, name, ArchiveFormat.Zip, ArchiveVolumeKind.ClassicZip, 1, out result))
        {
            return result;
        }

        if (name.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
        {
            return new ArchiveVolumeName(
                name,
                name[..^4],
                ArchiveFormat.Rar,
                ArchiveVolumeKind.SingleRar,
                null,
                0);
        }

        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return new ArchiveVolumeName(
                name,
                name[..^4],
                ArchiveFormat.Zip,
                ArchiveVolumeKind.SingleZip,
                null,
                0);
        }

        if (name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
        {
            return new ArchiveVolumeName(
                name,
                name[..^3],
                ArchiveFormat.SevenZip,
                ArchiveVolumeKind.SingleSevenZip,
                null,
                0);
        }

        return null;
    }

    private static bool TryCreateIndexed(
        Match match,
        string name,
        ArchiveFormat format,
        ArchiveVolumeKind kind,
        int minimum,
        out ArchiveVolumeName result)
    {
        if (match.Success &&
            int.TryParse(
                match.Groups["index"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var index) &&
            index >= minimum)
        {
            result = new ArchiveVolumeName(
                name,
                match.Groups["stem"].Value,
                format,
                kind,
                index,
                match.Groups["index"].Value.Length);
            return true;
        }

        result = null!;
        return false;
    }

    private static string GetPrimaryName(ArchiveVolumeName volume) =>
        volume.Kind switch
        {
            ArchiveVolumeKind.ModernRar => FormatIndexedName(volume, 1),
            ArchiveVolumeKind.LegacyRar => $"{volume.Stem}.rar",
            ArchiveVolumeKind.SplitSevenZip => FormatIndexedName(volume, 1),
            ArchiveVolumeKind.SplitZip => FormatIndexedName(volume, 1),
            ArchiveVolumeKind.ClassicZip => $"{volume.Stem}.zip",
            _ => volume.Name,
        };

    private static (string Parent, string Name) SplitLogicalPath(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath) ||
            !archivePath.StartsWith("/", StringComparison.Ordinal) ||
            archivePath.EndsWith("/", StringComparison.Ordinal))
        {
            throw new ArchiveUnsupportedException();
        }

        var separator = archivePath.LastIndexOf('/');
        var name = archivePath[(separator + 1)..];
        if (name.Length == 0 || name.Contains('\\'))
        {
            throw new ArchiveUnsupportedException();
        }

        return (separator == 0 ? "/" : archivePath[..separator], name);
    }

    private static void EnsureUniquePhysicalPaths(IReadOnlyList<ResolvedArchivePart> parts)
    {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        if (parts.Select(part => part.PhysicalPath).Distinct(comparer).Count() != parts.Count)
        {
            throw new ArchiveVolumeSetInvalidException([]);
        }
    }

    private static string JoinLogicalPath(string parent, string name) =>
        parent == "/" ? $"/{name}" : $"{parent}/{name}";

    [GeneratedRegex(@"^(?<stem>.+)\.part(?<index>\d+)\.rar$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ModernRarRegex();

    [GeneratedRegex(@"^(?<stem>.+)\.r(?<index>\d{2})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LegacyRarRegex();

    [GeneratedRegex(@"^(?<stem>.+)\.7z\.(?<index>\d{3})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SplitSevenZipRegex();

    [GeneratedRegex(@"^(?<stem>.+)\.zip\.(?<index>\d{3})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SplitZipRegex();

    [GeneratedRegex(@"^(?<stem>.+)\.z(?<index>\d{2,3})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ClassicZipRegex();

    private sealed record ArchiveVolumeName(
        string Name,
        string Stem,
        ArchiveFormat Format,
        ArchiveVolumeKind Kind,
        int? Index,
        int IndexWidth);

    private enum ArchiveVolumeKind
    {
        SingleZip,
        SingleRar,
        SingleSevenZip,
        ModernRar,
        LegacyRar,
        SplitSevenZip,
        SplitZip,
        ClassicZip,
    }
}

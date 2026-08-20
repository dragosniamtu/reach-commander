using System.Text.RegularExpressions;
using ReachCommander.ArchiveProtocol;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Common.Rar;

namespace ReachCommander.ArchiveWorker;

internal sealed record ArchiveInspectionResult(
    string Format,
    bool IsSolid,
    IReadOnlyList<ArchiveEntryFrame> Entries);

internal sealed partial class SharpCompressArchiveAdapter
{
    private const int MaximumVolumeCount = 256;
    private const int MaximumRequestEntries = 1_000_000;

    public ArchiveInspectionResult Inspect(ArchiveInspectionRequest request)
    {
        ValidateRequest(request);
        var files = request.VolumePaths.Select(path => new FileInfo(path)).ToArray();
        if (files.Any(file => !file.Exists))
        {
            throw WorkerFailure.Invalid(request.VolumePaths.Count > 1);
        }

        ValidateVolumeNames(files);
        try
        {
            var libraryFiles = IsClassicZip(files.Select(file => file.Name).ToArray())
                ? [files[^1], .. files[..^1]]
                : files;
            using var archive = ArchiveFactory.OpenArchive(libraryFiles);
            var format = MapFormat(archive.Type);
            var entries = ReadEntries(archive, request.Limits);
            if (!archive.IsComplete)
            {
                throw WorkerFailure.Invalid(request.VolumePaths.Count > 1);
            }

            ValidateConsumedVolumes(archive, files);
            return new ArchiveInspectionResult(format, archive.IsSolid, entries);
        }
        catch (WorkerFailure)
        {
            throw;
        }
        catch (CryptographicException)
        {
            throw WorkerFailure.Encrypted();
        }
        catch (SharpCompressException)
        {
            throw WorkerFailure.Invalid(request.VolumePaths.Count > 1);
        }
        catch (InvalidDataException)
        {
            throw WorkerFailure.Invalid(request.VolumePaths.Count > 1);
        }
        catch (IOException)
        {
            throw WorkerFailure.Invalid(request.VolumePaths.Count > 1);
        }
        catch (UnauthorizedAccessException)
        {
            throw WorkerFailure.Invalid(request.VolumePaths.Count > 1);
        }
    }

    private static IReadOnlyList<ArchiveEntryFrame> ReadEntries(
        IArchive archive,
        ArchiveWorkerLimits limits)
    {
        if (archive.IsEncrypted)
        {
            throw WorkerFailure.Encrypted();
        }

        var results = new List<ArchiveEntryFrame>();
        long totalSize = 0;
        foreach (var entry in archive.Entries)
        {
            if (results.Count >= limits.MaxEntries)
            {
                throw WorkerFailure.Limit();
            }

            if (entry.IsEncrypted)
            {
                throw WorkerFailure.Encrypted();
            }

            if (entry.Key is null || entry.Size < 0 || entry.CompressedSize < 0)
            {
                throw WorkerFailure.Invalid(volumeSet: false);
            }

            try
            {
                totalSize = checked(totalSize + (entry.IsDirectory ? 0 : entry.Size));
            }
            catch (OverflowException)
            {
                throw WorkerFailure.Limit();
            }

            if (totalSize > limits.MaxTotalExtractedBytes)
            {
                throw WorkerFailure.Limit();
            }

            var entryKind = GetEntryKind(entry);
            results.Add(new ArchiveEntryFrame(
                results.Count,
                entry.Key,
                entry.IsDirectory,
                entry.IsEncrypted,
                entryKind.IsLink,
                entryKind.IsSpecial,
                entry.IsDirectory ? null : entry.Size,
                entry.IsDirectory ? null : entry.CompressedSize,
                entry.LastModifiedTime is { } modified
                    ? new DateTimeOffset(modified)
                    : null));
        }

        return results.AsReadOnly();
    }

    private static (bool IsLink, bool IsSpecial) GetEntryKind(IArchiveEntry entry)
    {
        var isLink = entry.LinkTarget is not null;
        if (entry.Attrib is not { } attributes)
        {
            return (isLink, false);
        }

        var unixType = ((uint)attributes >> 16) & 0xF000;
        isLink |= unixType == 0xA000;
        var isSpecial = unixType is 0x1000 or 0x2000 or 0x6000 or 0xC000;
        return (isLink, isSpecial);
    }

    private static string MapFormat(ArchiveType type) => type switch
    {
        ArchiveType.Zip => "zip",
        ArchiveType.Rar => "rar",
        ArchiveType.SevenZip => "sevenZip",
        _ => throw WorkerFailure.Unsupported(),
    };

    private static void ValidateRequest(ArchiveInspectionRequest request)
    {
        if (request.ProtocolVersion != ArchiveFrameCodec.CurrentProtocolVersion ||
            string.IsNullOrWhiteSpace(request.RequestId) ||
            request.RequestId.Length > 128 ||
            request.VolumePaths is null ||
            request.Limits is null ||
            request.VolumePaths.Count is < 1 or > MaximumVolumeCount ||
            request.VolumePaths.Any(path =>
                string.IsNullOrWhiteSpace(path) ||
                !Path.IsPathFullyQualified(path)) ||
            request.VolumePaths.Distinct(PathComparer()).Count() != request.VolumePaths.Count ||
            request.Limits.MaxEntries is < 1 or > MaximumRequestEntries ||
            request.Limits.MaxTotalExtractedBytes < 1)
        {
            throw WorkerFailure.Protocol();
        }
    }

    private static void ValidateVolumeNames(IReadOnlyList<FileInfo> files)
    {
        if (files.Count == 1)
        {
            return;
        }

        var names = files.Select(file => file.Name).ToArray();
        if (IsModernRar(names) ||
            IsLegacyRar(names) ||
            IsNumberedSplit(names, ".7z.") ||
            IsNumberedSplit(names, ".zip.") ||
            IsClassicZip(names))
        {
            return;
        }

        throw WorkerFailure.VolumeSet();
    }

    private static bool IsModernRar(IReadOnlyList<string> names)
    {
        var first = ModernRarRegex().Match(names[0]);
        if (!first.Success || int.Parse(first.Groups["number"].Value) != 1)
        {
            return false;
        }

        var prefix = first.Groups["prefix"].Value;
        var width = first.Groups["number"].Value.Length;
        return names.Select((name, index) => (name, index)).All(pair =>
        {
            var match = ModernRarRegex().Match(pair.name);
            return match.Success &&
                match.Groups["prefix"].Value.Equals(prefix, StringComparison.OrdinalIgnoreCase) &&
                match.Groups["number"].Value.Length == width &&
                int.Parse(match.Groups["number"].Value) == pair.index + 1;
        });
    }

    private static bool IsLegacyRar(IReadOnlyList<string> names)
    {
        if (!names[0].EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var prefix = names[0][..^4];
        return names.Skip(1).Select((name, index) => (name, index)).All(pair =>
            pair.name.Equals(
                $"{prefix}.r{pair.index:00}",
                StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsNumberedSplit(
        IReadOnlyList<string> names,
        string marker)
    {
        var markerIndex = names[0].LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex <= 0)
        {
            return false;
        }

        var prefix = names[0][..(markerIndex + marker.Length)];
        var suffix = names[0][(markerIndex + marker.Length)..];
        if (suffix.Length < 2 || !int.TryParse(suffix, out var first) || first != 1)
        {
            return false;
        }

        return names.Select((name, index) => (name, index)).All(pair =>
            pair.name.Equals(
                $"{prefix}{pair.index + 1:D3}",
                StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsClassicZip(IReadOnlyList<string> names)
    {
        if (!names[^1].EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var prefix = names[^1][..^4];
        return names.Take(names.Count - 1).Select((name, index) => (name, index)).All(pair =>
            pair.name.Equals(
                $"{prefix}.z{pair.index + 1:00}",
                StringComparison.OrdinalIgnoreCase));
    }

    private static void ValidateConsumedVolumes(IArchive archive, IReadOnlyList<FileInfo> files)
    {
        if (files.Count == 1 || archive.Type == ArchiveType.SevenZip)
        {
            return;
        }

        var volumePaths = archive.Volumes
            .Select(volume => volume.FileName)
            .Where(path => path is not null)
            .Select(path => Path.GetFullPath(path!))
            .ToArray();
        var requested = files.Select(file => file.FullName).ToArray();
        if (!volumePaths.SequenceEqual(requested, PathComparer()))
        {
            throw WorkerFailure.VolumeSet();
        }

        if (archive.Type == ArchiveType.Rar)
        {
            var rarVolumes = archive.Volumes.Cast<RarVolume>().ToArray();
            if (rarVolumes.Any(volume =>
                !volume.IsMultiVolume ||
                volume.IsSolidArchive != archive.IsSolid))
            {
                throw WorkerFailure.VolumeSet();
            }
        }
    }

    private static StringComparer PathComparer() =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    [GeneratedRegex(
        @"^(?<prefix>.+)\.part(?<number>\d+)\.rar$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ModernRarRegex();
}

using System.Text.RegularExpressions;
using ReachCommander.Application.Files;
using ReachCommander.Application.Sources;

namespace ReachCommander.Infrastructure.Security;

public sealed partial class PathSecurityService(ISourceCatalog sourceCatalog) : IPathSecurityService
{
    public async ValueTask<ResolvedSourcePath> ResolveAsync(
        string sourceId,
        string logicalPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = await sourceCatalog.GetRequiredAsync(sourceId, cancellationToken);
        var normalized = Normalize(logicalPath);

        if (!Directory.Exists(source.RootPath))
        {
            throw new SourceUnavailableException(source.Id);
        }

        var canonicalRoot = ResolveAbsolutePath(source.RootPath, cancellationToken);
        if (!Directory.Exists(canonicalRoot))
        {
            throw new SourceUnavailableException(source.Id);
        }

        var current = canonicalRoot;
        foreach (var segment in Segments(normalized))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lexicalCandidate = Path.GetFullPath(Path.Combine(current, segment));
            EnsureWithin(canonicalRoot, lexicalCandidate, normalized);

            FileSystemInfo entry = GetExistingEntry(
                lexicalCandidate,
                source.Id,
                normalized);
            current = ResolveLink(entry);
            EnsureWithin(canonicalRoot, current, normalized);
        }

        return new ResolvedSourcePath(source, normalized, current);
    }

    private static string Normalize(string logicalPath)
    {
        if (string.IsNullOrEmpty(logicalPath))
        {
            throw new InvalidLogicalPathException(logicalPath, "it must start with a slash");
        }

        if (logicalPath.Contains('\0'))
        {
            throw new InvalidLogicalPathException(logicalPath, "null bytes are not allowed");
        }

        if (logicalPath.StartsWith("//", StringComparison.Ordinal) ||
            logicalPath.StartsWith("\\\\", StringComparison.Ordinal) ||
            DrivePathPattern().IsMatch(logicalPath))
        {
            throw new InvalidLogicalPathException(logicalPath, "physical rooted paths are not allowed");
        }

        if (!logicalPath.StartsWith("/", StringComparison.Ordinal))
        {
            throw new InvalidLogicalPathException(logicalPath, "it must start with a slash");
        }

        var segments = logicalPath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        var normalized = new List<string>(segments.Length);
        foreach (var segment in segments)
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                throw new InvalidLogicalPathException(logicalPath, "parent traversal is not allowed");
            }

            normalized.Add(segment);
        }

        return normalized.Count == 0 ? "/" : $"/{string.Join('/', normalized)}";
    }

    private static IEnumerable<string> Segments(string normalized) =>
        normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static string ResolveAbsolutePath(string absolutePath, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(absolutePath);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            throw new InvalidOperationException("A configured source root must be fully qualified.");
        }

        var current = root;
        var relative = Path.GetRelativePath(root, fullPath);
        foreach (var segment in relative.Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = Path.Combine(current, segment);
            FileSystemInfo entry = Directory.Exists(candidate)
                ? new DirectoryInfo(candidate)
                : new FileInfo(candidate);

            if (!entry.Exists)
            {
                return fullPath;
            }

            current = ResolveLink(entry);
        }

        return Path.GetFullPath(current);
    }

    private static FileSystemInfo GetExistingEntry(
        string physicalPath,
        string sourceId,
        string logicalPath)
    {
        FileSystemInfo entry = Directory.Exists(physicalPath)
            ? new DirectoryInfo(physicalPath)
            : new FileInfo(physicalPath);

        if (!entry.Exists)
        {
            throw new EntryNotFoundException(sourceId, logicalPath);
        }

        return entry;
    }

    private static string ResolveLink(FileSystemInfo entry)
    {
        if (entry.LinkTarget is null && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return Path.GetFullPath(entry.FullName);
        }

        var target = entry.ResolveLinkTarget(returnFinalTarget: true);
        return Path.GetFullPath(target?.FullName ?? entry.FullName);
    }

    private static void EnsureWithin(string root, string candidate, string logicalPath)
    {
        var relative = Path.GetRelativePath(root, candidate);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (Path.IsPathRooted(relative) ||
            relative.Equals("..", comparison) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", comparison))
        {
            throw new PathConfinementException(logicalPath);
        }
    }

    [GeneratedRegex("^/?[A-Za-z]:[\\\\/]")]
    private static partial Regex DrivePathPattern();
}

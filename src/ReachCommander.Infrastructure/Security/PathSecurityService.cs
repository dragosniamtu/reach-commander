using System.Text.RegularExpressions;
using ReachCommander.Application.Files;
using ReachCommander.Application.Sources;
using ReachCommander.Infrastructure.FileOperations;

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

    public async ValueTask<ResolvedSourcePath> ResolveChildAsync(
        string sourceId,
        string parentLogicalPath,
        string childName,
        CancellationToken cancellationToken)
    {
        ValidateChildName(childName, parentLogicalPath);
        var parent = await ResolveAsync(sourceId, parentLogicalPath, cancellationToken);
        if (!Directory.Exists(parent.PhysicalPath))
        {
            throw new InvalidLogicalPathException(
                parent.LogicalPath,
                "the parent is not a directory");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var candidate = Path.GetFullPath(Path.Combine(parent.PhysicalPath, childName));
        var canonicalRoot = ResolveAbsolutePath(parent.Source.RootPath, cancellationToken);
        EnsureWithin(canonicalRoot, candidate, parent.LogicalPath);
        var logicalPath = parent.LogicalPath == "/"
            ? $"/{childName}"
            : $"{parent.LogicalPath}/{childName}";
        return new ResolvedSourcePath(parent.Source, logicalPath, candidate);
    }

    public async ValueTask<ResolvedSourcePath> ResolveDescendantAsync(
        string sourceId,
        string parentLogicalPath,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var components = ValidateRelativeDescendant(relativePath, parentLogicalPath);
        var parent = await ResolveAsync(sourceId, parentLogicalPath, cancellationToken);
        if (!Directory.Exists(parent.PhysicalPath))
        {
            throw new InvalidLogicalPathException(
                parent.LogicalPath,
                "the parent is not a directory");
        }

        var canonicalRoot = ResolveAbsolutePath(parent.Source.RootPath, cancellationToken);
        var current = parent.PhysicalPath;
        foreach (var component in components)
        {
            cancellationToken.ThrowIfCancellationRequested();
            current = Path.GetFullPath(Path.Combine(current, component));
            EnsureWithin(canonicalRoot, current, parent.LogicalPath);
        }

        var logicalPath = parent.LogicalPath == "/"
            ? $"/{string.Join('/', components)}"
            : $"{parent.LogicalPath}/{string.Join('/', components)}";
        return new ResolvedSourcePath(parent.Source, logicalPath, current);
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

            if (ReservedFileOperationPathPolicy.IsReservedName(segment))
            {
                throw new InvalidLogicalPathException(
                    logicalPath,
                    "it uses a reserved ReachCommander name");
            }

            normalized.Add(segment);
        }

        return normalized.Count == 0 ? "/" : $"/{string.Join('/', normalized)}";
    }

    private static IEnumerable<string> Segments(string normalized) =>
        normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static void ValidateChildName(string childName, string parentLogicalPath)
    {
        if (string.IsNullOrEmpty(childName) ||
            childName is "." or ".." ||
            childName.Contains('/') ||
            childName.Contains('\\') ||
            childName.Contains(':') ||
            childName.Contains('\0') ||
            Path.IsPathRooted(childName))
        {
            throw new InvalidLogicalPathException(
                parentLogicalPath,
                "the child name must be one non-rooted path component");
        }

        if (ReservedFileOperationPathPolicy.IsReservedName(childName))
        {
            throw new InvalidLogicalPathException(
                parentLogicalPath,
                "the child name uses a reserved ReachCommander name");
        }
    }

    private static string[] ValidateRelativeDescendant(
        string relativePath,
        string parentLogicalPath)
    {
        if (string.IsNullOrEmpty(relativePath) ||
            relativePath.StartsWith('/') ||
            relativePath.EndsWith('/') ||
            relativePath.Contains("//", StringComparison.Ordinal) ||
            relativePath.Contains('\\') ||
            Path.IsPathRooted(relativePath))
        {
            throw new InvalidLogicalPathException(
                parentLogicalPath,
                "the descendant path must be normalized and relative");
        }

        var components = relativePath.Split('/', StringSplitOptions.None);
        foreach (var component in components)
        {
            ValidateChildName(component, parentLogicalPath);
        }

        return components;
    }

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

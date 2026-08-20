using System.Collections.ObjectModel;
using System.Text;
using ReachCommander.Application.Archives;
using ReachCommander.Domain.Archives;

namespace ReachCommander.Infrastructure.Archives.Catalog;

internal sealed record UntrustedArchiveEntry(
    int Index,
    string Key,
    bool IsDirectory,
    bool IsEncrypted,
    bool IsLink,
    bool IsSpecial,
    long? Size,
    long? CompressedSize,
    DateTimeOffset? ModifiedAt);

internal sealed record ArchiveCatalogNode(
    int? WorkerEntryIndex,
    string Path,
    string Name,
    ArchiveEntryType Type,
    long? Size,
    long? CompressedSize,
    DateTimeOffset? ModifiedAt,
    string? Extension,
    string Attributes,
    int DescendantFileCount,
    int DescendantDirectoryCount,
    long? DescendantSize,
    IReadOnlyList<string> Children);

internal sealed class ArchiveCatalog
{
    private readonly IReadOnlyDictionary<string, ArchiveCatalogNode> _nodes;

    public ArchiveCatalog(
        ArchiveFormat format,
        IDictionary<string, ArchiveCatalogNode> nodes,
        int fileCount,
        int directoryCount,
        long? totalDeclaredSize)
    {
        Format = format;
        _nodes = new ReadOnlyDictionary<string, ArchiveCatalogNode>(
            new Dictionary<string, ArchiveCatalogNode>(nodes, StringComparer.OrdinalIgnoreCase));
        FileCount = fileCount;
        DirectoryCount = directoryCount;
        TotalDeclaredSize = totalDeclaredSize;
    }

    public ArchiveFormat Format { get; }

    public IReadOnlyDictionary<string, ArchiveCatalogNode> Nodes => _nodes;

    public int FileCount { get; }

    public int DirectoryCount { get; }

    public long? TotalDeclaredSize { get; }

    public IReadOnlyList<ArchiveCatalogNode> ListChildren(string internalDirectory)
    {
        var normalized = NormalizeInternalPath(internalDirectory);
        if (!_nodes.TryGetValue(normalized, out var directory) ||
            directory.Type != ArchiveEntryType.Directory)
        {
            throw new ArchiveInvalidException();
        }

        return Array.AsReadOnly(directory.Children.Select(path => _nodes[path]).ToArray());
    }

    public IReadOnlyList<ArchiveCatalogNode> ExpandSelection(
        string internalDirectory,
        IReadOnlyList<string> selectedPaths,
        bool extractAll)
    {
        var current = NormalizeInternalPath(internalDirectory);
        if (!_nodes.TryGetValue(current, out var currentNode) ||
            currentNode.Type != ArchiveEntryType.Directory)
        {
            throw new ArchiveInvalidException();
        }

        IReadOnlyList<string> roots;
        if (extractAll)
        {
            if (current != "/" || selectedPaths.Count != 0)
            {
                throw new ArchiveEntryUnsafeException();
            }

            roots = currentNode.Children;
        }
        else
        {
            if (selectedPaths.Count == 0)
            {
                throw new ArchiveEntryUnsafeException();
            }

            var normalized = selectedPaths
                .Select(NormalizeInternalPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var path in normalized)
            {
                if (!_nodes.ContainsKey(path) || !IsAtOrBelow(path, current) || path == current)
                {
                    throw new ArchiveEntryUnsafeException();
                }
            }

            roots = normalized
                .Where(path => !normalized.Any(other =>
                    !other.Equals(path, StringComparison.OrdinalIgnoreCase) &&
                    IsDescendant(path, other)))
                .ToArray();
        }

        var expanded = new Dictionary<string, ArchiveCatalogNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            AddSubtree(root, expanded);
        }

        return Array.AsReadOnly(expanded.Values
            .OrderBy(node => node.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray());
    }

    private void AddSubtree(
        string path,
        IDictionary<string, ArchiveCatalogNode> destination)
    {
        var node = _nodes[path];
        if (!destination.TryAdd(node.Path, node))
        {
            return;
        }

        foreach (var child in node.Children)
        {
            AddSubtree(child, destination);
        }
    }

    private static string NormalizeInternalPath(string value)
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

    private static bool IsAtOrBelow(string candidate, string root) =>
        root == "/" ||
        candidate.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        IsDescendant(candidate, root);

    private static bool IsDescendant(string candidate, string ancestor) =>
        candidate.StartsWith($"{ancestor}/", StringComparison.OrdinalIgnoreCase);
}

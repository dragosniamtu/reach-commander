using Microsoft.Extensions.Options;
using ReachCommander.Application.Archives;
using ReachCommander.Domain.Archives;

namespace ReachCommander.Infrastructure.Archives.Catalog;

internal sealed class ArchiveCatalogBuilder
{
    private readonly ArchiveOptions _options;
    private readonly ArchivePathPolicy _pathPolicy;

    public ArchiveCatalogBuilder(IOptions<ArchiveOptions> options)
    {
        _options = options.Value;
        _pathPolicy = new ArchivePathPolicy(options);
    }

    public ArchiveCatalog Build(
        ArchiveFormat format,
        IEnumerable<UntrustedArchiveEntry> untrustedEntries)
    {
        var nodes = new Dictionary<string, MutableNode>(StringComparer.OrdinalIgnoreCase)
        {
            ["/"] = MutableNode.Directory("/", string.Empty, isSynthetic: false),
        };
        var indexes = new HashSet<int>();
        var entryCount = 0;
        long totalKnownSize = 0;
        var hasUnknownSize = false;
        long totalKnownCompressedSize = 0;

        foreach (var entry in untrustedEntries)
        {
            entryCount++;
            if (entryCount > _options.MaxEntries)
            {
                throw Limit("entry-count");
            }

            ValidateMetadata(entry, indexes);
            var rawKey = entry.IsDirectory
                ? entry.Key.TrimEnd('/', '\\')
                : entry.Key;
            var path = _pathPolicy.NormalizeEntryPath(rawKey);
            AddEntry(nodes, path, entry);
            if (nodes.Count - 1 > _options.MaxEntries)
            {
                throw Limit("entry-count");
            }

            if (entry.IsDirectory)
            {
                continue;
            }

            if (entry.Size is null)
            {
                hasUnknownSize = true;
            }
            else
            {
                if (entry.Size > _options.MaxSingleExtractedFileBytes)
                {
                    throw Limit("single-file size");
                }

                totalKnownSize = CheckedAdd(totalKnownSize, entry.Size.Value, "extracted-size");
                if (totalKnownSize > _options.MaxTotalExtractedBytes)
                {
                    throw Limit("extracted-size");
                }
            }

            if (entry.CompressedSize is not null)
            {
                totalKnownCompressedSize = CheckedAdd(
                    totalKnownCompressedSize,
                    entry.CompressedSize.Value,
                    "compressed-size");
                if (totalKnownCompressedSize > _options.MaxTotalCompressedBytes)
                {
                    throw Limit("compressed-size");
                }
            }

            ValidateExpansionRatio(entry);
        }

        CalculateAggregates(nodes, "/");
        var immutable = nodes.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToImmutable(),
            StringComparer.OrdinalIgnoreCase);
        var root = immutable["/"];
        return new ArchiveCatalog(
            format,
            immutable,
            root.DescendantFileCount,
            root.DescendantDirectoryCount,
            hasUnknownSize ? null : totalKnownSize);
    }

    private static void ValidateMetadata(
        UntrustedArchiveEntry entry,
        ISet<int> indexes)
    {
        if (!indexes.Add(entry.Index) ||
            entry.Index < 0 ||
            entry.Size < 0 ||
            entry.CompressedSize < 0)
        {
            throw new ArchiveEntryUnsafeException();
        }

        if (entry.IsEncrypted)
        {
            throw new ArchiveEncryptedException();
        }

        if (entry.IsLink || entry.IsSpecial)
        {
            throw new ArchiveEntryUnsafeException();
        }
    }

    private void AddEntry(
        IDictionary<string, MutableNode> nodes,
        string path,
        UntrustedArchiveEntry entry)
    {
        var components = path[1..].Split('/');
        var parentPath = "/";
        for (var index = 0; index < components.Length - 1; index++)
        {
            var component = components[index];
            var childPath = Join(parentPath, component);
            if (nodes.TryGetValue(childPath, out var existing))
            {
                EnsureExactCase(existing.Path, childPath);
                if (existing.Type != ArchiveEntryType.Directory)
                {
                    throw new ArchiveEntryUnsafeException();
                }
            }
            else
            {
                nodes[childPath] = MutableNode.Directory(
                    childPath,
                    component,
                    isSynthetic: true);
                nodes[parentPath].Children.Add(childPath);
            }

            parentPath = childPath;
        }

        var name = components[^1];
        if (nodes.TryGetValue(path, out var finalExisting))
        {
            EnsureExactCase(finalExisting.Path, path);
            if (entry.IsDirectory &&
                finalExisting.Type == ArchiveEntryType.Directory &&
                finalExisting.IsSynthetic)
            {
                finalExisting.IsSynthetic = false;
                finalExisting.ModifiedAt = entry.ModifiedAt;
                return;
            }

            throw new ArchiveEntryUnsafeException();
        }

        nodes[path] = entry.IsDirectory
            ? MutableNode.Directory(path, name, isSynthetic: false, entry.ModifiedAt)
            : MutableNode.File(path, name, entry);
        nodes[parentPath].Children.Add(path);
    }

    private void ValidateExpansionRatio(UntrustedArchiveEntry entry)
    {
        if (entry.Size is not > 0 || entry.CompressedSize is null)
        {
            return;
        }

        if (entry.CompressedSize == 0 ||
            entry.Size.Value / (double)entry.CompressedSize.Value > _options.MaxExpansionRatio)
        {
            throw Limit("expansion-ratio");
        }
    }

    private static Aggregate CalculateAggregates(
        IReadOnlyDictionary<string, MutableNode> nodes,
        string path)
    {
        var node = nodes[path];
        if (node.Type == ArchiveEntryType.File)
        {
            return new Aggregate(1, 0, node.Size);
        }

        var fileCount = 0;
        var directoryCount = 0;
        long size = 0;
        var unknownSize = false;
        foreach (var childPath in node.Children)
        {
            var child = nodes[childPath];
            var aggregate = CalculateAggregates(nodes, childPath);
            fileCount = checked(fileCount + aggregate.FileCount);
            directoryCount = checked(
                directoryCount + aggregate.DirectoryCount +
                (child.Type == ArchiveEntryType.Directory ? 1 : 0));
            if (aggregate.Size is null)
            {
                unknownSize = true;
            }
            else
            {
                size = checked(size + aggregate.Size.Value);
            }
        }

        node.DescendantFileCount = fileCount;
        node.DescendantDirectoryCount = directoryCount;
        node.DescendantSize = unknownSize ? null : size;
        return new Aggregate(fileCount, directoryCount, node.DescendantSize);
    }

    private static long CheckedAdd(long left, long right, string limit)
    {
        try
        {
            return checked(left + right);
        }
        catch (OverflowException)
        {
            throw Limit(limit);
        }
    }

    private static ArchiveLimitExceededException Limit(string limit) =>
        new($"The archive exceeds the configured {limit} limit.");

    private static void EnsureExactCase(string existing, string candidate)
    {
        if (!existing.Equals(candidate, StringComparison.Ordinal))
        {
            throw new ArchiveEntryUnsafeException();
        }
    }

    private static string Join(string parent, string name) =>
        parent == "/" ? $"/{name}" : $"{parent}/{name}";

    private sealed class MutableNode
    {
        private MutableNode(
            int? workerEntryIndex,
            string path,
            string name,
            ArchiveEntryType type,
            long? size,
            long? compressedSize,
            DateTimeOffset? modifiedAt,
            bool isSynthetic)
        {
            WorkerEntryIndex = workerEntryIndex;
            Path = path;
            Name = name;
            Type = type;
            Size = size;
            CompressedSize = compressedSize;
            ModifiedAt = modifiedAt;
            IsSynthetic = isSynthetic;
        }

        public int? WorkerEntryIndex { get; }

        public string Path { get; }

        public string Name { get; }

        public ArchiveEntryType Type { get; }

        public long? Size { get; }

        public long? CompressedSize { get; }

        public DateTimeOffset? ModifiedAt { get; set; }

        public bool IsSynthetic { get; set; }

        public SortedSet<string> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int DescendantFileCount { get; set; }

        public int DescendantDirectoryCount { get; set; }

        public long? DescendantSize { get; set; }

        public static MutableNode Directory(
            string path,
            string name,
            bool isSynthetic,
            DateTimeOffset? modifiedAt = null) =>
            new(
                null,
                path,
                name,
                ArchiveEntryType.Directory,
                null,
                null,
                modifiedAt,
                isSynthetic);

        public static MutableNode File(
            string path,
            string name,
            UntrustedArchiveEntry entry) =>
            new(
                entry.Index,
                path,
                name,
                ArchiveEntryType.File,
                entry.Size,
                entry.CompressedSize,
                entry.ModifiedAt,
                isSynthetic: false);

        public ArchiveCatalogNode ToImmutable() => new(
            WorkerEntryIndex,
            Path,
            Name,
            Type,
            Size,
            CompressedSize,
            ModifiedAt,
            Type == ArchiveEntryType.File ? GetExtension(Name) : null,
            "Archive · RO",
            DescendantFileCount,
            DescendantDirectoryCount,
            DescendantSize,
            Array.AsReadOnly(Children.ToArray()));

        private static string? GetExtension(string name)
        {
            var separator = name.LastIndexOf('.');
            return separator <= 0 || separator == name.Length - 1
                ? null
                : name[(separator + 1)..];
        }
    }

    private sealed record Aggregate(int FileCount, int DirectoryCount, long? Size);
}

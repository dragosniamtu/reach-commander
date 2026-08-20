using Microsoft.Extensions.Options;
using ReachCommander.Domain.Archives;
using ReachCommander.Infrastructure.Archives.Volumes;

namespace ReachCommander.Infrastructure.Archives.Catalog;

internal sealed class ArchiveCatalogCache
{
    private readonly object _gate = new();
    private readonly ArchiveOptions _options;
    private readonly TimeProvider _clock;
    private readonly Dictionary<CacheKey, CacheEntry> _entries = new();
    private long _accessSequence;
    private int _aggregateNodeCount;

    public ArchiveCatalogCache(
        IOptions<ArchiveOptions> options,
        TimeProvider clock)
    {
        _options = options.Value;
        _clock = clock;
    }

    internal int AggregateNodeCount
    {
        get
        {
            lock (_gate)
            {
                return _aggregateNodeCount;
            }
        }
    }

    public ArchiveCatalog? Get(
        ArchiveFormat format,
        ArchiveVolumeFingerprint fingerprint)
    {
        lock (_gate)
        {
            RemoveExpired();
            var key = new CacheKey(format, fingerprint.Value);
            if (!_entries.TryGetValue(key, out var entry))
            {
                return null;
            }

            entry.LastAccess = ++_accessSequence;
            return entry.Catalog;
        }
    }

    public void Set(
        ArchiveFormat format,
        ArchiveVolumeFingerprint fingerprint,
        ArchiveCatalog catalog)
    {
        var nodeCount = catalog.Nodes.Count;
        if (nodeCount > _options.MaxCachedEntries)
        {
            return;
        }

        lock (_gate)
        {
            RemoveExpired();
            var key = new CacheKey(format, fingerprint.Value);
            if (_entries.Remove(key, out var replaced))
            {
                _aggregateNodeCount -= replaced.NodeCount;
            }

            var entry = new CacheEntry(
                catalog,
                _clock.GetUtcNow(),
                ++_accessSequence,
                nodeCount);
            _entries[key] = entry;
            _aggregateNodeCount += nodeCount;
            EvictToLimits();
        }
    }

    private void RemoveExpired()
    {
        var now = _clock.GetUtcNow();
        var expired = _entries
            .Where(pair => now - pair.Value.CreatedAt >= _options.CatalogLifetime)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var key in expired)
        {
            Remove(key);
        }
    }

    private void EvictToLimits()
    {
        while (_entries.Count > _options.MaxCachedCatalogs ||
               _aggregateNodeCount > _options.MaxCachedEntries)
        {
            var oldest = _entries.MinBy(pair => pair.Value.LastAccess).Key;
            Remove(oldest);
        }
    }

    private void Remove(CacheKey key)
    {
        if (_entries.Remove(key, out var entry))
        {
            _aggregateNodeCount -= entry.NodeCount;
        }
    }

    private sealed record CacheKey(ArchiveFormat Format, string Fingerprint);

    private sealed class CacheEntry(
        ArchiveCatalog catalog,
        DateTimeOffset createdAt,
        long lastAccess,
        int nodeCount)
    {
        public ArchiveCatalog Catalog { get; } = catalog;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public long LastAccess { get; set; } = lastAccess;
        public int NodeCount { get; } = nodeCount;
    }
}

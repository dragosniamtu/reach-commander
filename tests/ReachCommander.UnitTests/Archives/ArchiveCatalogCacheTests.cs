using Microsoft.Extensions.Options;
using ReachCommander.Domain.Archives;
using ReachCommander.Infrastructure.Archives;
using ReachCommander.Infrastructure.Archives.Catalog;
using ReachCommander.Infrastructure.Archives.Volumes;

namespace ReachCommander.UnitTests.Archives;

public sealed class ArchiveCatalogCacheTests
{
    [Fact]
    public void Returns_only_an_exact_format_and_fingerprint_match()
    {
        var cache = CreateCache();
        var catalog = Catalog("one.txt");

        cache.Set(ArchiveFormat.Zip, Fingerprint("one"), catalog);

        Assert.Same(catalog, cache.Get(ArchiveFormat.Zip, Fingerprint("one")));
        Assert.Null(cache.Get(ArchiveFormat.Rar, Fingerprint("one")));
        Assert.Null(cache.Get(ArchiveFormat.Zip, Fingerprint("two")));
    }

    [Fact]
    public void Expires_entries_at_the_absolute_lifetime_even_after_access()
    {
        var clock = new ManualTimeProvider();
        var cache = CreateCache(clock: clock);
        cache.Set(ArchiveFormat.Zip, Fingerprint("one"), Catalog("one.txt"));

        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.NotNull(cache.Get(ArchiveFormat.Zip, Fingerprint("one")));
        clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Null(cache.Get(ArchiveFormat.Zip, Fingerprint("one")));
    }

    [Fact]
    public void Evicts_the_least_recently_used_catalog_at_the_count_limit()
    {
        var cache = CreateCache(new ArchiveOptions
        {
            MaxCachedCatalogs = 2,
            MaxCachedEntries = 10,
            MaxEntries = 10,
        });
        cache.Set(ArchiveFormat.Zip, Fingerprint("one"), Catalog("one.txt"));
        cache.Set(ArchiveFormat.Zip, Fingerprint("two"), Catalog("two.txt"));
        Assert.NotNull(cache.Get(ArchiveFormat.Zip, Fingerprint("one")));

        cache.Set(ArchiveFormat.Zip, Fingerprint("three"), Catalog("three.txt"));

        Assert.NotNull(cache.Get(ArchiveFormat.Zip, Fingerprint("one")));
        Assert.Null(cache.Get(ArchiveFormat.Zip, Fingerprint("two")));
        Assert.NotNull(cache.Get(ArchiveFormat.Zip, Fingerprint("three")));
    }

    [Fact]
    public void Evicts_until_the_aggregate_node_limit_is_satisfied()
    {
        var cache = CreateCache(new ArchiveOptions
        {
            MaxCachedCatalogs = 4,
            MaxCachedEntries = 5,
            MaxEntries = 5,
        });
        cache.Set(ArchiveFormat.Zip, Fingerprint("one"), Catalog("a/one.txt"));

        cache.Set(ArchiveFormat.Zip, Fingerprint("two"), Catalog("b/two.txt"));

        Assert.Null(cache.Get(ArchiveFormat.Zip, Fingerprint("one")));
        Assert.NotNull(cache.Get(ArchiveFormat.Zip, Fingerprint("two")));
        Assert.True(cache.AggregateNodeCount <= 5);
    }

    private static ArchiveCatalogCache CreateCache(
        ArchiveOptions? options = null,
        TimeProvider? clock = null) =>
        new(
            Options.Create(options ?? new ArchiveOptions()),
            clock ?? TimeProvider.System);

    private static ArchiveCatalog Catalog(string key) =>
        new ArchiveCatalogBuilder(Options.Create(new ArchiveOptions())).Build(
            ArchiveFormat.Zip,
            [new UntrustedArchiveEntry(0, key, false, false, false, false, 1, 1, null)]);

    private static ArchiveVolumeFingerprint Fingerprint(string value) => new(value);

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 8, 20, 8, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now += amount;
    }
}

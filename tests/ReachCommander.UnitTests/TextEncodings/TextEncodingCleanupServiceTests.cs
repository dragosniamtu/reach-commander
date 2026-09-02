using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ReachCommander.Infrastructure.TextEncodings;
using ReachCommander.UnitTests.Support;

namespace ReachCommander.UnitTests.TextEncodings;

public sealed class TextEncodingCleanupServiceTests
{
    [Fact]
    public async Task Registry_writes_atomic_logical_only_manifest_and_can_remove_it()
    {
        using var fixture = new TextEncodingTestFixture();
        var registry = Registry(fixture);

        var record = await registry.RegisterAsync(
            "media",
            "/TV",
            ".reachcommander-operation-encoding-abc-000.partial",
            CancellationToken.None);

        var manifestPath = registry.GetManifestPath(record.RecordId);
        Assert.True(File.Exists(manifestPath));
        var json = File.ReadAllText(manifestPath);
        Assert.DoesNotContain(fixture.SourceRoot, json, StringComparison.Ordinal);
        Assert.DoesNotContain("physical", json, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFiles(registry.RegistryDirectory, "*.tmp"));

        registry.Remove(record.RecordId);
        Assert.False(File.Exists(manifestPath));
    }

    [Fact]
    public async Task Cleanup_preserves_young_staging_and_removes_it_after_twenty_four_hours()
    {
        using var fixture = new TextEncodingTestFixture();
        var registry = Registry(fixture);
        const string stagingName = ".reachcommander-operation-encoding-abc-000.partial";
        fixture.WriteUtf8($"TV/{stagingName}", "partial");
        var record = await registry.RegisterAsync(
            "media",
            "/TV",
            stagingName,
            CancellationToken.None);

        await Cleanup(fixture, registry).StartAsync(CancellationToken.None);
        Assert.True(File.Exists(fixture.PhysicalPath($"TV/{stagingName}")));
        Assert.True(File.Exists(registry.GetManifestPath(record.RecordId)));

        fixture.Clock.Advance(TimeSpan.FromHours(24));
        await Cleanup(fixture, registry).StartAsync(CancellationToken.None);
        Assert.False(File.Exists(fixture.PhysicalPath($"TV/{stagingName}")));
        Assert.False(File.Exists(registry.GetManifestPath(record.RecordId)));
    }

    [Fact]
    public async Task Cleanup_quarantines_manifest_with_non_private_staging_name()
    {
        using var fixture = new TextEncodingTestFixture();
        var registry = Registry(fixture);
        Directory.CreateDirectory(registry.RegistryDirectory);
        var record = new TextEncodingStagingRecord(
            Guid.NewGuid(),
            "media",
            "/TV",
            "ordinary.srt",
            fixture.Clock.GetUtcNow().AddHours(-25));
        var manifestPath = registry.GetManifestPath(record.RecordId);
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(record));
        fixture.WriteUtf8("TV/ordinary.srt", "do not delete");

        await Cleanup(fixture, registry).StartAsync(CancellationToken.None);

        Assert.True(File.Exists(fixture.PhysicalPath("TV/ordinary.srt")));
        Assert.False(File.Exists(manifestPath));
        Assert.Single(Directory.EnumerateFiles(registry.RegistryDirectory, "*.invalid*"));
    }

    [Fact]
    public async Task Cleanup_treats_missing_source_as_already_cleaned()
    {
        using var fixture = new TextEncodingTestFixture();
        var registry = Registry(fixture);
        var record = await registry.RegisterAsync(
            "missing",
            "/TV",
            ".reachcommander-operation-encoding-missing-000.partial",
            CancellationToken.None);
        fixture.Clock.Advance(TimeSpan.FromHours(24));

        await Cleanup(fixture, registry).StartAsync(CancellationToken.None);

        Assert.False(File.Exists(registry.GetManifestPath(record.RecordId)));
    }

    [Fact]
    public async Task Cleanup_does_not_scan_source_for_unregistered_staging_files()
    {
        using var fixture = new TextEncodingTestFixture();
        var registry = Registry(fixture);
        const string stagingName = ".reachcommander-operation-encoding-unregistered-000.partial";
        fixture.WriteUtf8($"TV/{stagingName}", "keep");
        fixture.Clock.Advance(TimeSpan.FromHours(48));

        await Cleanup(fixture, registry).StartAsync(CancellationToken.None);

        Assert.True(File.Exists(fixture.PhysicalPath($"TV/{stagingName}")));
    }

    private static TextEncodingStagingRegistry Registry(TextEncodingTestFixture fixture) => new(
        fixture.AuthenticationPaths,
        fixture.Clock,
        NullLogger<TextEncodingStagingRegistry>.Instance);

    private static TextEncodingCleanupService Cleanup(
        TextEncodingTestFixture fixture,
        TextEncodingStagingRegistry registry) => new(
            registry,
            fixture.PathSecurity,
            fixture.FileSystem,
            fixture.Clock,
            NullLogger<TextEncodingCleanupService>.Instance);
}

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ReachCommander.Application.SystemUpdates;
using ReachCommander.Infrastructure.SystemUpdates;

namespace ReachCommander.UnitTests.SystemUpdates;

public sealed class SystemUpdateSupportBundleServiceTests
{
    [Fact]
    public async Task Create_builds_only_the_five_sanitized_entries()
    {
        var service = new SystemUpdateSupportBundleService(
            new FixedGateway(HealthySnapshot()),
            new FixedTimeProvider());

        var result = await service.CreateAsync(CancellationToken.None);

        Assert.Equal("reachcommander-support-20260827T120000Z.zip", result.FileName);
        using var archive = new ZipArchive(new MemoryStream(result.Content));
        Assert.Equal(
            ["README.txt", "deployment-health.json", "manifest.json", "summary.txt", "update-trace.json"],
            archive.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal).ToArray());
        Assert.True(archive.Entries.Sum(entry => entry.Length) <= 1_048_576);
        var allText = string.Join("\n", archive.Entries.Select(Read));
        Assert.DoesNotContain("sha256:", allText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/srv/", allText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sudo reachcommander doctor", allText);
    }

    [Fact]
    public async Task Create_returns_a_partial_bundle_when_the_host_helper_is_unavailable()
    {
        var service = new SystemUpdateSupportBundleService(
            new ThrowingGateway(),
            new FixedTimeProvider());

        var result = await service.CreateAsync(CancellationToken.None);

        using var archive = new ZipArchive(new MemoryStream(result.Content));
        using var manifest = JsonDocument.Parse(Read(archive.GetEntry("manifest.json")!));
        Assert.False(manifest.RootElement.GetProperty("hostSnapshotComplete").GetBoolean());
        Assert.Contains("refresh the checksum-verified Ubuntu installer", Read(archive.GetEntry("summary.txt")!));
    }

    private static SystemUpdateDiagnosticsSnapshot HealthySnapshot() => new(
        1,
        DateTimeOffset.Parse("2026-08-27T12:00:00Z"),
        true,
        4,
        "stable",
        "v1.4.0",
        null,
        null,
        SystemUpdateDiagnostics.CheckNames.Select(name =>
            new SystemUpdateDiagnosticCheck(
                name,
                SystemUpdateDiagnosticStatus.Healthy,
                SystemUpdateDiagnostics.ReasonCode(name, SystemUpdateDiagnosticStatus.Healthy))).ToArray());

    private static string Read(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private sealed class FixedGateway(SystemUpdateDiagnosticsSnapshot snapshot)
        : ISystemUpdateDiagnosticsGateway
    {
        public Task<SystemUpdateDiagnosticsSnapshot> CollectAsync(CancellationToken cancellationToken) =>
            Task.FromResult(snapshot);
    }

    private sealed class ThrowingGateway : ISystemUpdateDiagnosticsGateway
    {
        public Task<SystemUpdateDiagnosticsSnapshot> CollectAsync(CancellationToken cancellationToken) =>
            throw new SystemUpdaterUnavailableException("host detail must not be copied");
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            DateTimeOffset.Parse("2026-08-27T12:00:00Z");
    }
}

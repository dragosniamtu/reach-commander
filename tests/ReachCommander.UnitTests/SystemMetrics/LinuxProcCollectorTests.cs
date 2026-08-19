using Microsoft.Extensions.Options;
using ReachCommander.Application.SystemMetrics;
using ReachCommander.Infrastructure.SystemMetrics;
using ReachCommander.Infrastructure.SystemMetrics.Linux;
using ReachCommander.UnitTests.Support;

namespace ReachCommander.UnitTests.SystemMetrics;

public sealed class LinuxProcCollectorTests : IDisposable
{
    private readonly TemporaryDirectory _temporary = new();

    [Fact]
    public async Task Collect_maps_memory_uptime_and_delta_cpu_network_values()
    {
        WriteSnapshot("cpu 100 0 100 800 0 0 0 0", 1000, 2000);
        var clock = new ManualMetricsTimeProvider(
            new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero));
        var collector = CreateCollector(clock);

        await collector.CollectAsync(CancellationToken.None);
        WriteSnapshot("cpu 150 0 150 900 0 0 0 0", 1500, 3000);
        clock.Advance(TimeSpan.FromSeconds(5));

        var result = await collector.CollectAsync(CancellationToken.None);

        Assert.Equal(HardwareCollectorState.Success, result.Status.State);
        Assert.Equal(50, result.Cpu?.UtilizationPercent);
        Assert.Equal(1_024_000, result.Memory?.TotalBytes);
        Assert.Equal(614_400, result.Memory?.UsedBytes);
        Assert.Equal(123, result.HostUptimeSeconds);
        Assert.Equal(100, result.Network?.ReceiveBytesPerSecond);
        Assert.Equal(200, result.Network?.TransmitBytesPerSecond);
    }

    [Fact]
    public async Task Collect_returns_failed_status_for_oversized_input_without_raw_text()
    {
        Write("stat", new string('x', BoundedTextFileReader.MaximumFileCharacters + 1));

        var result = await CreateCollector(new ManualMetricsTimeProvider(DateTimeOffset.UtcNow))
            .CollectAsync(CancellationToken.None);

        Assert.Equal(HardwareCollectorState.Failed, result.Status.State);
        Assert.Equal("metrics_input_invalid", result.Status.Code);
        Assert.DoesNotContain(_temporary.Path, result.Status.Code, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Collect_returns_null_cpu_and_zero_rates_after_counter_reset_or_zero_total_delta()
    {
        WriteSnapshot("cpu 100 0 100 800 0 0 0 0", 1000, 2000);
        var clock = new ManualMetricsTimeProvider(DateTimeOffset.UtcNow);
        var collector = CreateCollector(clock);
        await collector.CollectAsync(CancellationToken.None);
        WriteSnapshot("cpu 100 0 100 800 0 0 0 0", 10, 20);
        clock.Advance(TimeSpan.FromSeconds(5));

        var result = await collector.CollectAsync(CancellationToken.None);

        Assert.Null(result.Cpu?.UtilizationPercent);
        Assert.Equal(0, result.Network?.ReceiveBytesPerSecond);
        Assert.Equal(0, result.Network?.TransmitBytesPerSecond);
    }

    [Fact]
    public async Task Collect_succeeds_without_optional_network_file()
    {
        Write("stat", "cpu 100 0 100 800 0 0 0 0\n");
        Write("meminfo", "MemTotal: 1000 kB\nMemAvailable: 400 kB\n");
        Write("uptime", "123.45 88.00\n");

        var result = await CreateCollector(new ManualMetricsTimeProvider(DateTimeOffset.UtcNow))
            .CollectAsync(CancellationToken.None);

        Assert.Equal(HardwareCollectorState.Success, result.Status.State);
        Assert.Null(result.Network);
    }

    [Fact]
    public async Task Collect_returns_unsupported_without_touching_files_on_non_linux_platforms()
    {
        var collector = new LinuxProcCollector(
            Options.Create(new HardwareMetricsOptions { LinuxProcRoot = "missing" }),
            new BoundedTextFileReader(),
            TimeProvider.System,
            StubHostPlatform.Other);

        var result = await collector.CollectAsync(CancellationToken.None);

        Assert.Equal(HardwareCollectorState.Unsupported, result.Status.State);
    }

    private LinuxProcCollector CreateCollector(TimeProvider clock) => new(
        Options.Create(new HardwareMetricsOptions { LinuxProcRoot = _temporary.Path }),
        new BoundedTextFileReader(),
        clock,
        StubHostPlatform.Linux);

    private void WriteSnapshot(string cpu, long receive, long transmit)
    {
        Write("stat", $"{cpu}\n");
        Write("meminfo", "MemTotal: 1000 kB\nMemAvailable: 400 kB\n");
        Write("uptime", "123.45 88.00\n");
        Write(
            "net/dev",
            $"Inter-| Receive | Transmit\n face |bytes packets errs drop fifo frame compressed multicast|bytes packets errs drop fifo colls carrier compressed\nlo: 20 0 0 0 0 0 0 0 20 0 0 0 0 0 0 0\neth0: {receive} 0 0 0 0 0 0 0 {transmit} 0 0 0 0 0 0 0\n");
    }

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_temporary.Path, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose() => _temporary.Dispose();
}

internal sealed class ManualMetricsTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan amount) => _utcNow += amount;
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ReachCommander.Application.SystemMetrics;

namespace ReachCommander.IntegrationTests;

public sealed class SystemMetricsApiTests(ReachCommanderApiFactory factory)
    : IClassFixture<ReachCommanderApiFactory>
{
    [Fact]
    public async Task Get_returns_normalized_snapshot_without_host_sensitive_fields()
    {
        factory.SetHardwareSnapshot(new HardwareMetricsSnapshot(
            new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero),
            HardwareMetricsState.Partial,
            3600,
            new CpuMetrics(25, 55, 90, 100, false, false),
            new MemoryMetrics(60, 40, 100, 60),
            [new StorageMetrics("media", "Media", true, 75, 25, 100, 75)],
            [new GpuMetrics("gpu-nvidia-001", "NVIDIA", "GPU Test", 40, 2, 8, 60, null, null, false, false)],
            [new FanMetrics("fan-001", "CPU Fan", 1400, false, false)],
            new NetworkMetrics(1000, 500),
            [new HardwareCollectorStatus("gpu", HardwareCollectorState.Unavailable, "gpu_partial")]));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/system-metrics");
        var body = await response.Content.ReadAsStringAsync();
        var snapshot = await response.Content.ReadFromJsonAsync<SystemMetricsResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("partial", snapshot?.State);
        Assert.Equal(25, snapshot?.Cpu?.UtilizationPercent);
        Assert.Equal("gpu-nvidia-001", Assert.Single(snapshot!.Gpus).Id);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal(3600, root.GetProperty("hostUptimeSeconds").GetInt64());
        Assert.Equal(60, root.GetProperty("memory").GetProperty("utilizationPercent").GetDouble());
        Assert.Equal("media", root.GetProperty("storage")[0].GetProperty("sourceId").GetString());
        Assert.Equal(1400, root.GetProperty("fans")[0].GetProperty("revolutionsPerMinute").GetInt32());
        Assert.Equal(1000, root.GetProperty("network").GetProperty("receiveBytesPerSecond").GetInt64());
        Assert.Equal("gpu_partial", root.GetProperty("collectors")[0].GetProperty("code").GetString());
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.DoesNotContain(factory.WorkspaceRoot, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rootPath", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("physicalPath", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pci", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("serial", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hostname", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("process", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("commandLine", body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(HardwareMetricsState.Stale, "stale")]
    [InlineData(HardwareMetricsState.Disabled, "disabled")]
    public async Task Get_serializes_effective_states_as_lowercase_strings(
        HardwareMetricsState state,
        string expected)
    {
        factory.SetHardwareSnapshot(EmptySnapshot(state));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/system-metrics");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, json.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Unknown_system_metrics_subroute_remains_json_not_found()
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/system-metrics/unknown");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Get_returns_safe_problem_details_before_first_sample()
    {
        factory.SetHardwareNotReady();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/system-metrics");
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("metrics_not_ready", problem?.Code);
    }

    private sealed record SystemMetricsResponse(
        string State,
        CpuResponse? Cpu,
        IReadOnlyList<GpuResponse> Gpus);

    private sealed record CpuResponse(double? UtilizationPercent);
    private sealed record GpuResponse(string Id);
    private sealed record ProblemResponse(string Code);

    private static HardwareMetricsSnapshot EmptySnapshot(HardwareMetricsState state) =>
        new(DateTimeOffset.UtcNow, state, null, null, null, [], [], [], null, []);
}

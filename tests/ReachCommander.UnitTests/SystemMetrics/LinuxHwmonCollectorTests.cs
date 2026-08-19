using Microsoft.Extensions.Options;
using ReachCommander.Application.SystemMetrics;
using ReachCommander.Infrastructure.SystemMetrics;
using ReachCommander.Infrastructure.SystemMetrics.Linux;
using ReachCommander.UnitTests.Support;

namespace ReachCommander.UnitTests.SystemMetrics;

public sealed class LinuxHwmonCollectorTests : IDisposable
{
    private readonly TemporaryDirectory _temporary = new();

    [Fact]
    public async Task Collect_selects_cpu_package_temperature_and_maps_fans_thresholds_and_faults()
    {
        Write("class/hwmon/hwmon0/name", "coretemp\n");
        Write("class/hwmon/hwmon0/temp1_label", "Package id 0\n");
        Write("class/hwmon/hwmon0/temp1_input", "55000\n");
        Write("class/hwmon/hwmon0/temp1_max", "90000\n");
        Write("class/hwmon/hwmon0/temp1_crit", "100000\n");
        Write("class/hwmon/hwmon0/fan1_label", "CPU Fan\n");
        Write("class/hwmon/hwmon0/fan1_input", "1350\n");
        Write("class/hwmon/hwmon0/fan1_alarm", "0\n");
        Write("class/hwmon/hwmon0/fan1_fault", "0\n");

        var result = await CreateCollector().CollectAsync(CancellationToken.None);

        Assert.Equal(HardwareCollectorState.Success, result.Status.State);
        Assert.Equal(55, result.Cpu?.TemperatureCelsius);
        Assert.Equal(90, result.Cpu?.WarningTemperatureCelsius);
        Assert.Equal(100, result.Cpu?.CriticalTemperatureCelsius);
        var fan = Assert.Single(result.Fans!);
        Assert.Equal("CPU Fan", fan.Name);
        Assert.Equal(1350, fan.RevolutionsPerMinute);
        Assert.False(fan.Alarm);
        Assert.False(fan.Fault);
    }

    [Fact]
    public async Task Collect_omits_faulted_value_and_caps_sensor_count()
    {
        Write("class/hwmon/hwmon0/name", "nct6798\n");
        Write("class/hwmon/hwmon0/fan1_input", "900\n");
        Write("class/hwmon/hwmon0/fan1_fault", "1\n");
        for (var index = 2; index <= 140; index++)
        {
            Write($"class/hwmon/hwmon0/fan{index}_input", "1000\n");
        }

        var result = await CreateCollector().CollectAsync(CancellationToken.None);

        Assert.Null(result.Fans![0].RevolutionsPerMinute);
        Assert.True(result.Fans[0].Fault);
        Assert.True(result.Fans.Count <= MetricNormalizer.MaximumSensorsPerFamily);
    }

    [Fact]
    public async Task Collect_uses_a_labeled_thermal_zone_fallback()
    {
        Write("class/thermal/thermal_zone0/type", "x86_pkg_temp\n");
        Write("class/thermal/thermal_zone0/temp", "57000\n");
        Write("class/thermal/thermal_zone0/trip_point_0_type", "passive\n");
        Write("class/thermal/thermal_zone0/trip_point_0_temp", "90000\n");
        Write("class/thermal/thermal_zone0/trip_point_1_type", "critical\n");
        Write("class/thermal/thermal_zone0/trip_point_1_temp", "100000\n");

        var result = await CreateCollector().CollectAsync(CancellationToken.None);

        Assert.Equal(57, result.Cpu?.TemperatureCelsius);
        Assert.Equal(90, result.Cpu?.WarningTemperatureCelsius);
        Assert.Equal(100, result.Cpu?.CriticalTemperatureCelsius);
    }

    [Fact]
    public async Task Collect_deduplicates_aliases_that_resolve_to_the_same_device()
    {
        Write("class/hwmon/hwmon0/name", "nct6798\n");
        Write("class/hwmon/hwmon0/fan1_input", "1200\n");
        Write("class/hwmon/hwmon1/name", "nct6798\n");
        Write("class/hwmon/hwmon1/fan1_input", "1200\n");
        var resolver = new AliasingPathResolver(_temporary.Path);

        var result = await CreateCollector(resolver).CollectAsync(CancellationToken.None);

        Assert.Single(result.Fans!);
    }

    [Fact]
    public async Task Collect_returns_unsupported_when_no_sensor_roots_exist()
    {
        var result = await CreateCollector().CollectAsync(CancellationToken.None);

        Assert.Equal(HardwareCollectorState.Unsupported, result.Status.State);
    }

    private LinuxHwmonCollector CreateCollector(ITrustedPathResolver? resolver = null) => new(
        Options.Create(new HardwareMetricsOptions { LinuxSysRoot = _temporary.Path }),
        new BoundedTextFileReader(),
        resolver ?? new TrustedPathResolver(StubHostPlatform.Linux),
        StubHostPlatform.Linux);

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_temporary.Path, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose() => _temporary.Dispose();

    private sealed class AliasingPathResolver(string root) : ITrustedPathResolver
    {
        public string GetCanonicalPath(string path) =>
            Path.GetFileName(path).StartsWith("hwmon", StringComparison.Ordinal)
                ? Path.Combine(root, "devices", "same-chip")
                : Path.GetFullPath(path);

        public bool IsWithinRoot(string trustedRoot, string candidate) => true;
    }
}

# ReachCommander Live Hardware Monitoring Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a safe, cross-platform, five-second live host-health widget for CPU, RAM, configured-source storage, GPU, fan, network, and uptime metrics.

**Architecture:** .NET application contracts expose one immutable cached snapshot while isolated Windows, Linux, storage, and GPU collectors feed a singleton background sampler. Angular polls one read-only API route and renders a compact top-right summary plus an accessible details panel; missing collectors remain independent unavailable values.

**Tech Stack:** .NET 10 / C# 14, ASP.NET Core hosted services and controllers, LibreHardwareMonitorLib 0.9.6, Linux procfs/sysfs/DRM, optional NVIDIA NVML, Angular 22 standalone components and Signals, Vitest, xUnit, and Playwright Chromium.

## Global Constraints

- Work directly on `master`; do not create a Git worktree.
- Preserve .NET SDK `10.0.400`, Angular `22.1`, TypeScript strict mode, Node `24.15+` or `22.22.3+`, and npm `10.9.2` requirements.
- Use stable `LibreHardwareMonitorLib` version `0.9.6`; add no other runtime dependency unless a failing approved requirement proves it necessary.
- Sample on server startup and every 5 seconds; mark the effective snapshot stale after 15 seconds; use a 2-second per-collector aggregation deadline.
- Support native Windows development and native or Dockerized Ubuntu deployment without a separate host agent.
- Auto-detect NVIDIA, AMD, and Intel adapters, but treat temperatures, fans, and vendor GPU fields as best-effort nullable values.
- Keep the feature read-only: no fan, thermal, power, clock, GPU, procfs, sysfs, or device-control writes.
- Never require `privileged: true`, `SYS_ADMIN`, `/dev/mem`, a writable sysfs mount, or the Docker socket.
- Browser and API data must exclude configured roots, physical paths, proc/sys paths, hostnames, PCI identifiers, serial numbers, process information, raw native errors, and command output.
- Use fixed trusted configuration for Linux proc/sys roots. No HTTP request may select a filesystem or native-library path.
- Do not invoke shells or construct external commands; NVIDIA uses dynamically loaded NVML and AMD/Intel use read-only sysfs/DRM attributes.
- Bound file reads, sensor counts, GPU counts, fan counts, label lengths, and all numeric values before caching or serializing.
- Keep five-second numeric updates out of live regions; announce only material state transitions.
- Preserve the hardened default Compose path. Rich host telemetry is an explicit `compose.hardware.yaml` override.
- Use TDD for every production slice: observe the focused test fail before writing its implementation.
- Before every commit, inspect `git status --short` and stage only files changed for that task; never absorb unrelated user edits.
- Before completion, run backend, Angular, Playwright, publish, repository-hygiene, Windows-native startup when available, and Docker configuration/runtime checks. Report unavailable environments honestly.

## File Structure

```text
src/ReachCommander.Application/SystemMetrics/
├── HardwareMetricsExceptions.cs        Stable not-ready failure
├── HardwareMetricsSnapshot.cs          Public immutable platform-neutral records
└── IHardwareMetricsSnapshotProvider.cs Cached snapshot read port

src/ReachCommander.Infrastructure/SystemMetrics/
├── HardwareMetricsOptions.cs           Validated trusted collection configuration
├── HardwareMetricsOptionsValidator.cs  Startup invariants
├── HardwareMetricsContribution.cs      Internal collector contribution model
├── HardwareMetricsSampler.cs           Five-second aggregation hosted service
├── HardwareMetricsSnapshotCache.cs     Atomic last-good/effective-stale cache
├── HostPlatform.cs                     Injectable runtime OS boundary
├── IHardwareMetricsCollector.cs        Isolated collector boundary
├── MetricNormalizer.cs                 Labels, units, finite/range validation
├── SourceStorageCollector.cs           Live capacity for configured sources
├── TrustedPathResolver.cs              Symlink confinement and identity
├── Linux/
│   ├── BoundedTextFileReader.cs         Fixed-root bounded file access
│   ├── LinuxProcCollector.cs            CPU/RAM/network/uptime delta reader
│   └── LinuxHwmonCollector.cs           CPU temperature and fan reader
├── Gpu/
│   ├── GpuVendor.cs                     Vendor identifiers and safe adapter IDs
│   ├── LinuxDrmGpuCollector.cs          AMD/Intel DRM/hwmon reader
│   ├── NvidiaNvmlApi.cs                 Dynamic native NVML boundary
│   └── NvidiaNvmlCollector.cs           NVIDIA snapshot mapper
└── Windows/
    ├── LibreHardwareMonitorAdapter.cs   Third-party sensor-tree boundary
    └── WindowsHardwareCollector.cs      Approved Windows metric mapping

src/ReachCommander.Api/
├── Contracts/SystemMetrics/SystemMetricsDto.cs
└── Controllers/SystemMetricsController.cs

client/reach-commander-ui/src/app/
├── core/api/api.models.ts
├── core/api/reach-commander-api.ts
├── core/state/system-metrics-store.ts
├── features/system-metrics/
│   ├── system-metrics-widget.component.{ts,html,scss,spec.ts}
│   └── system-metrics-details.component.{ts,html,scss,spec.ts}
└── shared/pipes/byte-rate.pipe.{ts,spec.ts}

tests/ReachCommander.UnitTests/SystemMetrics/
├── BoundedHardwareCollectorRunnerTests.cs
├── HardwareMetricsOptionsValidatorTests.cs
├── HardwareMetricsSamplerTests.cs
├── LinuxHwmonCollectorTests.cs
├── LinuxProcCollectorTests.cs
├── LinuxGpuCollectorTests.cs
├── NativeNvidiaNvmlApiTests.cs
├── MetricNormalizerTests.cs
├── SourceStorageCollectorTests.cs
├── TrustedPathResolverTests.cs
├── LibreHardwareMonitorAdapterTests.cs
└── WindowsHardwareCollectorTests.cs

tests/ReachCommander.IntegrationTests/SystemMetricsApiTests.cs
tests/e2e/specs/system-metrics.spec.ts
compose.hardware.yaml
compose.hardware.dri.yaml
compose.hardware.nvidia.yaml
```

---

### Task 1: Platform-neutral contracts, trusted options, and normalization

**Files:**

- Create: `src/ReachCommander.Application/SystemMetrics/HardwareMetricsSnapshot.cs`
- Create: `src/ReachCommander.Application/SystemMetrics/IHardwareMetricsSnapshotProvider.cs`
- Create: `src/ReachCommander.Application/SystemMetrics/HardwareMetricsExceptions.cs`
- Create: `src/ReachCommander.Infrastructure/SystemMetrics/HardwareMetricsOptions.cs`
- Create: `src/ReachCommander.Infrastructure/SystemMetrics/HardwareMetricsOptionsValidator.cs`
- Create: `src/ReachCommander.Infrastructure/SystemMetrics/MetricNormalizer.cs`
- Create or modify: `src/ReachCommander.Infrastructure/Properties/AssemblyInfo.cs`
- Modify: `src/ReachCommander.Api/appsettings.json`
- Test: `tests/ReachCommander.UnitTests/SystemMetrics/HardwareMetricsOptionsValidatorTests.cs`
- Test: `tests/ReachCommander.UnitTests/SystemMetrics/MetricNormalizerTests.cs`

**Interfaces:**

- Produces: public `HardwareMetricsSnapshot` and every nested record/enum shown below.
- Produces: `IHardwareMetricsSnapshotProvider.GetCurrent()` and `HardwareMetricsNotReadyException`.
- Produces: internal `HardwareMetricsOptions`, `HardwareMetricsOptionsValidator`, and `MetricNormalizer` consumed by Tasks 2-10.
- Consumes: no feature code; this is the root contract slice.

- [ ] **Step 1: Write failing options and normalizer tests**

Create exact boundary tests:

```csharp
using Microsoft.Extensions.Options;
using ReachCommander.Infrastructure.SystemMetrics;

namespace ReachCommander.UnitTests.SystemMetrics;

public sealed class HardwareMetricsOptionsValidatorTests
{
    private readonly HardwareMetricsOptionsValidator _validator = new();

    [Fact]
    public void Validate_accepts_the_approved_defaults()
    {
        var result = _validator.Validate(null, new HardwareMetricsOptions());

        Assert.False(result.Failed);
    }

    [Theory]
    [InlineData(4, 15, 2000)]
    [InlineData(5, 5, 2000)]
    [InlineData(5, 15, 5000)]
    public void Validate_rejects_unsafe_timing_combinations(
        int sampleSeconds,
        int staleSeconds,
        int timeoutMilliseconds)
    {
        var options = new HardwareMetricsOptions
        {
            SampleIntervalSeconds = sampleSeconds,
            StaleAfterSeconds = staleSeconds,
            CollectorTimeoutMilliseconds = timeoutMilliseconds,
        };

        Assert.True(_validator.Validate(null, options).Failed);
    }

    [Theory]
    [InlineData("proc")]
    [InlineData("../proc")]
    [InlineData("")]
    public void Validate_rejects_non_absolute_linux_roots(string root)
    {
        var options = new HardwareMetricsOptions { LinuxProcRoot = root };

        Assert.True(_validator.Validate(null, options).Failed);
    }
}
```

```csharp
using ReachCommander.Infrastructure.SystemMetrics;

namespace ReachCommander.UnitTests.SystemMetrics;

public sealed class MetricNormalizerTests
{
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(43.126, 43.1)]
    [InlineData(101, 100)]
    public void Percent_clamps_and_rounds_finite_values(double input, double expected) =>
        Assert.Equal(expected, MetricNormalizer.Percent(input));

    [Fact]
    public void Percent_rejects_non_finite_values()
    {
        Assert.Null(MetricNormalizer.Percent(double.NaN));
        Assert.Null(MetricNormalizer.Percent(double.PositiveInfinity));
    }

    [Fact]
    public void Label_removes_controls_bounds_length_and_uses_fallback()
    {
        Assert.Equal("CPU Fan", MetricNormalizer.Label(" CPU\0 Fan ", "Fan"));
        Assert.Equal("Fan", MetricNormalizer.Label(" \0 ", "Fan"));
        Assert.Equal(96, MetricNormalizer.Label(new string('x', 120), "Fan").Length);
    }

    [Fact]
    public void Non_negative_integer_rejects_values_that_are_unsafe_in_json_clients()
    {
        Assert.Equal(0, MetricNormalizer.NonNegative(0));
        Assert.Equal(9_007_199_254_740_991, MetricNormalizer.NonNegative(9_007_199_254_740_991));
        Assert.Null(MetricNormalizer.NonNegative(-1));
        Assert.Null(MetricNormalizer.NonNegative(long.MaxValue));
    }

    [Theory]
    [InlineData(55000, 55.0)]
    [InlineData(-1000, -1.0)]
    public void Millidegrees_converts_to_celsius(long input, double expected) =>
        Assert.Equal(expected, MetricNormalizer.MillidegreesCelsius(input));
}
```

- [ ] **Step 2: Run focused tests and verify RED**

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter "FullyQualifiedName~HardwareMetricsOptionsValidatorTests|FullyQualifiedName~MetricNormalizerTests"
```

Expected: compilation fails because the system-metrics contracts, options, validator, and normalizer do not exist.

- [ ] **Step 3: Add the exact application contracts**

Create `HardwareMetricsSnapshot.cs`:

```csharp
namespace ReachCommander.Application.SystemMetrics;

public enum HardwareMetricsState { Healthy, Partial, Stale, Disabled }
public enum HardwareCollectorState { Success, Unsupported, Unavailable, Timeout, Failed }

public sealed record CpuMetrics(
    double? UtilizationPercent,
    double? TemperatureCelsius,
    double? WarningTemperatureCelsius,
    double? CriticalTemperatureCelsius,
    bool Alarm,
    bool Fault);

public sealed record MemoryMetrics(
    long? UsedBytes,
    long? AvailableBytes,
    long? TotalBytes,
    double? UtilizationPercent);

public sealed record StorageMetrics(
    string SourceId,
    string Name,
    bool IsAvailable,
    long? UsedBytes,
    long? FreeBytes,
    long? TotalBytes,
    double? UtilizationPercent);

public sealed record GpuMetrics(
    string Id,
    string Vendor,
    string Name,
    double? UtilizationPercent,
    long? MemoryUsedBytes,
    long? MemoryTotalBytes,
    double? TemperatureCelsius,
    double? WarningTemperatureCelsius,
    double? CriticalTemperatureCelsius,
    bool Alarm,
    bool Fault);

public sealed record FanMetrics(
    string Id,
    string Name,
    int? RevolutionsPerMinute,
    bool Alarm,
    bool Fault);

public sealed record NetworkMetrics(
    long? ReceiveBytesPerSecond,
    long? TransmitBytesPerSecond);

public sealed record HardwareCollectorStatus(
    string Collector,
    HardwareCollectorState State,
    string? Code);

public sealed record HardwareMetricsSnapshot(
    DateTimeOffset SampledAt,
    HardwareMetricsState State,
    long? HostUptimeSeconds,
    CpuMetrics? Cpu,
    MemoryMetrics? Memory,
    IReadOnlyList<StorageMetrics> Storage,
    IReadOnlyList<GpuMetrics> Gpus,
    IReadOnlyList<FanMetrics> Fans,
    NetworkMetrics? Network,
    IReadOnlyList<HardwareCollectorStatus> Collectors);
```

Create the provider and exception:

```csharp
namespace ReachCommander.Application.SystemMetrics;

public interface IHardwareMetricsSnapshotProvider
{
    HardwareMetricsSnapshot GetCurrent();
}
```

```csharp
namespace ReachCommander.Application.SystemMetrics;

public sealed class HardwareMetricsNotReadyException()
    : Exception("Hardware metrics have not completed their first sample.")
{
}
```

- [ ] **Step 4: Implement trusted options and exact validation**

```csharp
namespace ReachCommander.Infrastructure.SystemMetrics;

internal sealed class HardwareMetricsOptions
{
    public const string SectionName = "HardwareMetrics";
    public bool Enabled { get; init; } = true;
    public int SampleIntervalSeconds { get; init; } = 5;
    public int StaleAfterSeconds { get; init; } = 15;
    public int CollectorTimeoutMilliseconds { get; init; } = 2000;
    public string LinuxProcRoot { get; init; } = "/proc";
    public string LinuxSysRoot { get; init; } = "/sys";
    public bool TemperaturesEnabled { get; init; } = true;
    public bool FansEnabled { get; init; } = true;
    public bool NetworkEnabled { get; init; } = true;
    public bool GpusEnabled { get; init; } = true;
}
```

```csharp
using Microsoft.Extensions.Options;

namespace ReachCommander.Infrastructure.SystemMetrics;

internal sealed class HardwareMetricsOptionsValidator : IValidateOptions<HardwareMetricsOptions>
{
    public ValidateOptionsResult Validate(string? name, HardwareMetricsOptions options)
    {
        var failures = new List<string>();
        if (options.SampleIntervalSeconds < 5)
            failures.Add("HardwareMetrics:SampleIntervalSeconds must be at least 5.");
        if (options.StaleAfterSeconds <= options.SampleIntervalSeconds)
            failures.Add("HardwareMetrics:StaleAfterSeconds must exceed the sample interval.");
        if (options.CollectorTimeoutMilliseconds <= 0 ||
            options.CollectorTimeoutMilliseconds >= options.SampleIntervalSeconds * 1000)
            failures.Add("HardwareMetrics:CollectorTimeoutMilliseconds must be positive and shorter than the sample interval.");
        if (!IsTrustedAbsoluteRoot(options.LinuxProcRoot))
            failures.Add("HardwareMetrics:LinuxProcRoot must be absolute.");
        if (!IsTrustedAbsoluteRoot(options.LinuxSysRoot))
            failures.Add("HardwareMetrics:LinuxSysRoot must be absolute.");

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static bool IsTrustedAbsoluteRoot(string value) =>
        value.StartsWith('/', StringComparison.Ordinal) || Path.IsPathFullyQualified(value);
}
```

Add the explicit defaults to `appsettings.json`:

```json
"HardwareMetrics": {
  "Enabled": true,
  "SampleIntervalSeconds": 5,
  "StaleAfterSeconds": 15,
  "CollectorTimeoutMilliseconds": 2000,
  "LinuxProcRoot": "/proc",
  "LinuxSysRoot": "/sys",
  "TemperaturesEnabled": true,
  "FansEnabled": true,
  "NetworkEnabled": true,
  "GpusEnabled": true
}
```

- [ ] **Step 5: Implement bounded normalization**

```csharp
namespace ReachCommander.Infrastructure.SystemMetrics;

internal static class MetricNormalizer
{
    public const int MaximumLabelLength = 96;
    public const int MaximumSensorsPerFamily = 128;
    public const int MaximumGpuCount = 16;
    public const long MaximumSafeJsonInteger = 9_007_199_254_740_991;

    public static double? Percent(double? value) =>
        value is null || !double.IsFinite(value.Value)
            ? null
            : Math.Round(Math.Clamp(value.Value, 0, 100), 1, MidpointRounding.AwayFromZero);

    public static double? Celsius(double? value) =>
        value is null || !double.IsFinite(value.Value) || value is < -100 or > 250
            ? null
            : Math.Round(value.Value, 1, MidpointRounding.AwayFromZero);

    public static double? MillidegreesCelsius(long value) => Celsius(value / 1000d);

    public static long? NonNegative(long? value) =>
        value is >= 0 and <= MaximumSafeJsonInteger ? value : null;

    public static string Label(string? value, string fallback)
    {
        var cleaned = new string((value ?? string.Empty)
            .Where(character => !char.IsControl(character))
            .ToArray()).Trim();
        if (cleaned.Length == 0)
            cleaned = fallback;
        return cleaned.Length <= MaximumLabelLength
            ? cleaned
            : cleaned[..MaximumLabelLength];
    }
}
```

Add `[assembly: InternalsVisibleTo("ReachCommander.UnitTests")]` to `AssemblyInfo.cs`, preserving any existing identical attribute from the Multi-Rename implementation.

- [ ] **Step 6: Run focused and full unit tests**

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter "FullyQualifiedName~HardwareMetricsOptionsValidatorTests|FullyQualifiedName~MetricNormalizerTests"
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release
```

Expected: focused tests and all existing unit tests pass with zero warnings.

- [ ] **Step 7: Commit the contract slice**

```powershell
git status --short
git add src/ReachCommander.Application/SystemMetrics src/ReachCommander.Infrastructure/SystemMetrics/HardwareMetricsOptions.cs src/ReachCommander.Infrastructure/SystemMetrics/HardwareMetricsOptionsValidator.cs src/ReachCommander.Infrastructure/SystemMetrics/MetricNormalizer.cs src/ReachCommander.Infrastructure/Properties/AssemblyInfo.cs src/ReachCommander.Api/appsettings.json tests/ReachCommander.UnitTests/SystemMetrics/HardwareMetricsOptionsValidatorTests.cs tests/ReachCommander.UnitTests/SystemMetrics/MetricNormalizerTests.cs
git commit -m "feat: define hardware metrics contracts"
```

---

### Task 2: Linux CPU, RAM, uptime, and network collector

**Files:**

- Create: `src/ReachCommander.Infrastructure/SystemMetrics/HardwareMetricsContribution.cs`
- Create: `src/ReachCommander.Infrastructure/SystemMetrics/HostPlatform.cs`
- Create: `src/ReachCommander.Infrastructure/SystemMetrics/IHardwareMetricsCollector.cs`
- Create: `src/ReachCommander.Infrastructure/SystemMetrics/TrustedPathResolver.cs`
- Create: `src/ReachCommander.Infrastructure/SystemMetrics/Linux/BoundedTextFileReader.cs`
- Create: `src/ReachCommander.Infrastructure/SystemMetrics/Linux/LinuxProcCollector.cs`
- Test: `tests/ReachCommander.UnitTests/SystemMetrics/LinuxProcCollectorTests.cs`
- Test: `tests/ReachCommander.UnitTests/SystemMetrics/TrustedPathResolverTests.cs`
- Create: `tests/ReachCommander.UnitTests/Support/StubHostPlatform.cs`
- Use: `tests/ReachCommander.UnitTests/Support/TemporaryDirectory.cs`

**Interfaces:**

- Consumes: Task 1 `HardwareMetricsOptions`, `MetricNormalizer`, `CpuMetrics`, `MemoryMetrics`, `NetworkMetrics`, and collector status enums.
- Produces: internal `IHardwareMetricsCollector`, `HardwareMetricsContribution`, `BoundedTextFileReader`, and `LinuxProcCollector` for Task 6.
- `LinuxProcCollector` owns previous CPU/network counters and never overlaps its own collection.
- `TrustedPathResolver` confines and deduplicates later hwmon/DRM traversals without exposing canonical paths.

- [ ] **Step 1: Write failing Linux proc tests with fixture files**

```csharp
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
        Write("stat", "cpu 100 0 100 800 0 0 0 0\n");
        Write("meminfo", "MemTotal: 1000 kB\nMemAvailable: 400 kB\n");
        Write("uptime", "123.45 88.00\n");
        Write("net/dev", "Inter-| Receive | Transmit\n face |bytes packets errs drop fifo frame compressed multicast|bytes packets errs drop fifo colls carrier compressed\nlo: 20 0 0 0 0 0 0 0 20 0 0 0 0 0 0 0\neth0: 1000 0 0 0 0 0 0 0 2000 0 0 0 0 0 0 0\n");
        var clock = new ManualMetricsTimeProvider(new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero));
        var collector = CreateCollector(clock);

        await collector.CollectAsync(CancellationToken.None);
        Write("stat", "cpu 150 0 150 900 0 0 0 0\n");
        Write("net/dev", "Inter-| Receive | Transmit\n face |bytes packets errs drop fifo frame compressed multicast|bytes packets errs drop fifo colls carrier compressed\nlo: 999 0 0 0 0 0 0 0 999 0 0 0 0 0 0 0\neth0: 1500 0 0 0 0 0 0 0 3000 0 0 0 0 0 0 0\n");
        clock.Advance(TimeSpan.FromSeconds(5));

        var result = await collector.CollectAsync(CancellationToken.None);

        Assert.Equal(50, result.Cpu?.UtilizationPercent);
        Assert.Equal(1_024_000, result.Memory?.TotalBytes);
        Assert.Equal(614_400, result.Memory?.UsedBytes);
        Assert.Equal(123, result.HostUptimeSeconds);
        Assert.Equal(100, result.Network?.ReceiveBytesPerSecond);
        Assert.Equal(200, result.Network?.TransmitBytesPerSecond);
    }

    [Fact]
    public async Task Collect_returns_failed_status_for_oversized_or_malformed_input_without_raw_text()
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
        WriteValidSnapshot(cpu: "cpu 100 0 100 800 0 0 0 0", receive: 1000, transmit: 2000);
        var clock = new ManualMetricsTimeProvider(DateTimeOffset.UtcNow);
        var collector = CreateCollector(clock);
        await collector.CollectAsync(CancellationToken.None);
        WriteValidSnapshot(cpu: "cpu 100 0 100 800 0 0 0 0", receive: 10, transmit: 20);
        clock.Advance(TimeSpan.FromSeconds(5));

        var result = await collector.CollectAsync(CancellationToken.None);

        Assert.Null(result.Cpu?.UtilizationPercent);
        Assert.Equal(0, result.Network?.ReceiveBytesPerSecond);
        Assert.Equal(0, result.Network?.TransmitBytesPerSecond);
    }

    private LinuxProcCollector CreateCollector(TimeProvider clock) => new(
        Options.Create(new HardwareMetricsOptions { LinuxProcRoot = _temporary.Path }),
        new BoundedTextFileReader(),
        clock,
        StubHostPlatform.Linux);

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_temporary.Path, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void WriteValidSnapshot(string cpu, long receive, long transmit)
    {
        Write("stat", $"{cpu}\n");
        Write("meminfo", "MemTotal: 1000 kB\nMemAvailable: 400 kB\n");
        Write("uptime", "123.45 88.00\n");
        Write("net/dev", $"Inter-| Receive | Transmit\n face |bytes packets errs drop fifo frame compressed multicast|bytes packets errs drop fifo colls carrier compressed\neth0: {receive} 0 0 0 0 0 0 0 {transmit} 0 0 0 0 0 0 0\n");
    }

    public void Dispose() => _temporary.Dispose();
}

internal sealed class ManualMetricsTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = utcNow;
    public override DateTimeOffset GetUtcNow() => _utcNow;
    public void Advance(TimeSpan amount) => _utcNow += amount;
}
```

- [ ] **Step 2: Run the collector tests and verify RED**

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter FullyQualifiedName~LinuxProcCollectorTests
```

Expected: compilation fails because the collector boundary, contribution, reader, and Linux proc collector do not exist.

- [ ] **Step 3: Add the internal collector contract**

```csharp
using ReachCommander.Application.SystemMetrics;

namespace ReachCommander.Infrastructure.SystemMetrics;

internal interface IHardwareMetricsCollector
{
    string Name { get; }
    bool IsSupported { get; }
    ValueTask<HardwareMetricsContribution> CollectAsync(CancellationToken cancellationToken);
}

internal sealed record HardwareMetricsContribution(
    HardwareCollectorStatus Status,
    long? HostUptimeSeconds = null,
    CpuMetrics? Cpu = null,
    MemoryMetrics? Memory = null,
    IReadOnlyList<StorageMetrics>? Storage = null,
    IReadOnlyList<GpuMetrics>? Gpus = null,
    IReadOnlyList<FanMetrics>? Fans = null,
    NetworkMetrics? Network = null)
{
    public static HardwareMetricsContribution Unsupported(string collector) => new(
        new HardwareCollectorStatus(collector, HardwareCollectorState.Unsupported, "collector_unsupported"));
}
```

Create the injectable platform boundary in `HostPlatform.cs`:

```csharp
namespace ReachCommander.Infrastructure.SystemMetrics;

internal interface IHostPlatform
{
    bool IsLinux { get; }
    bool IsWindows { get; }
}

internal sealed class RuntimeHostPlatform : IHostPlatform
{
    public bool IsLinux => OperatingSystem.IsLinux();
    public bool IsWindows => OperatingSystem.IsWindows();
}
```

Create the exact test boundary in `StubHostPlatform.cs`:

```csharp
using ReachCommander.Infrastructure.SystemMetrics;

namespace ReachCommander.UnitTests.Support;

internal sealed class StubHostPlatform(bool isLinux, bool isWindows) : IHostPlatform
{
    public static StubHostPlatform Linux { get; } = new(true, false);
    public static StubHostPlatform Windows { get; } = new(false, true);
    public bool IsLinux { get; } = isLinux;
    public bool IsWindows { get; } = isWindows;
}
```

Every OS-specific collector accepts `IHostPlatform`, derives `IsSupported` from it plus its feature flag, and uses that boundary instead of directly calling `OperatingSystem.IsLinux/IsWindows`. Native adapters may still guard their platform-specific library calls defensively. This keeps fixture tests hardware- and runner-independent.

- [ ] **Step 4: Implement bounded fixed-file reads**

```csharp
namespace ReachCommander.Infrastructure.SystemMetrics.Linux;

internal sealed class BoundedTextFileReader
{
    public const int MaximumFileCharacters = 1_048_576;

    public async ValueTask<string> ReadRequiredAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream);
        var buffer = new char[MaximumFileCharacters + 1];
        var count = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);
        if (count > MaximumFileCharacters)
            throw new InvalidDataException("Hardware metrics input exceeds its read limit.");
        return new string(buffer, 0, count);
    }
}
```

All paths passed to this reader are constructed internally from the validated options roots and fixed filenames.

Add `ITrustedPathResolver.GetCanonicalPath(path)` and `IsWithinRoot(root, candidate)`. `TrustedPathResolver` uses `FileSystemInfo.ResolveLinkTarget(returnFinalTarget: true)` when a link exists, falls back to `Path.GetFullPath`, and delegates to an internal pure `IsCanonicalPathWithinRoot(canonicalRoot, canonicalCandidate, comparison)` helper. That helper uses `Path.GetRelativePath`; a candidate is contained only when the result is neither rooted nor `..` nor prefixed by `..` plus the platform separator. Use `Ordinal` on Linux and `OrdinalIgnoreCase` on Windows. Unit-test normal containment, a sibling-prefix escape, and an outside canonical target through the pure helper; no test needs to create an OS symlink.

- [ ] **Step 5: Implement exact proc parsing and delta rules**

`LinuxProcCollector` must:

1. Return `Unsupported("linux-proc")` when `IHostPlatform.IsLinux` is false.
2. Read only `stat`, `meminfo`, `uptime`, and `net/dev` beneath `LinuxProcRoot`.
3. Parse aggregate `cpu` counters as unsigned 64-bit values. Treat idle plus iowait as idle and calculate `100 * (deltaTotal - deltaIdle) / deltaTotal`; the first or reset sample returns null utilization.
4. Require positive `MemTotal` and `MemAvailable` in KiB, checked-multiply by 1024, clamp available to total, and derive used/percentage.
5. Parse the first finite non-negative uptime value and floor it to whole seconds.
6. Sum non-loopback interface byte counters with checked arithmetic. Divide positive deltas by elapsed seconds; reset/removed counters return zero for that interval.
7. Return `Success` with nullable fields for absent optional network data. Map bounded I/O/format/overflow failures to `Failed` plus `metrics_input_unavailable` or `metrics_input_invalid`, never raw text.
8. Cap parsed lines at 4,096 and network interfaces at 256.

Use invariant parsing and `MetricNormalizer` for final percent values. Keep previous counters and timestamp under a private lock so direct concurrent calls cannot corrupt deltas.

- [ ] **Step 6: Run focused and full backend tests**

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter "FullyQualifiedName~LinuxProcCollectorTests|FullyQualifiedName~TrustedPathResolverTests"
dotnet test ReachCommander.slnx -c Release
```

Expected: proc fixture/path-confinement tests and the full solution pass.

- [ ] **Step 7: Commit Linux base metrics**

```powershell
git status --short
git add src/ReachCommander.Infrastructure/SystemMetrics/HardwareMetricsContribution.cs src/ReachCommander.Infrastructure/SystemMetrics/HostPlatform.cs src/ReachCommander.Infrastructure/SystemMetrics/IHardwareMetricsCollector.cs src/ReachCommander.Infrastructure/SystemMetrics/TrustedPathResolver.cs src/ReachCommander.Infrastructure/SystemMetrics/Linux/BoundedTextFileReader.cs src/ReachCommander.Infrastructure/SystemMetrics/Linux/LinuxProcCollector.cs tests/ReachCommander.UnitTests/SystemMetrics/LinuxProcCollectorTests.cs tests/ReachCommander.UnitTests/SystemMetrics/TrustedPathResolverTests.cs tests/ReachCommander.UnitTests/Support/StubHostPlatform.cs
git commit -m "feat: collect Linux host base metrics"
```

---

### Task 3: Linux hwmon sensors and configured-source storage

**Files:**

- Create: `src/ReachCommander.Infrastructure/SystemMetrics/Linux/LinuxHwmonCollector.cs`
- Create: `src/ReachCommander.Infrastructure/SystemMetrics/SourceStorageCollector.cs`
- Test: `tests/ReachCommander.UnitTests/SystemMetrics/LinuxHwmonCollectorTests.cs`
- Test: `tests/ReachCommander.UnitTests/SystemMetrics/SourceStorageCollectorTests.cs`

**Interfaces:**

- Consumes: Task 1 normalization/options and Task 2 collector/contribution contracts.
- Consumes: existing `ISourceCatalog.GetSnapshotsAsync(CancellationToken)` and safe `SourceSnapshot` capacity fields.
- Produces: `LinuxHwmonCollector` and `SourceStorageCollector` for Task 6 aggregation.

- [ ] **Step 1: Write failing hwmon fixture tests**

```csharp
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
        var collector = new LinuxHwmonCollector(
            Options.Create(new HardwareMetricsOptions { LinuxSysRoot = _temporary.Path }),
            new BoundedTextFileReader(),
            new TrustedPathResolver(StubHostPlatform.Linux),
            StubHostPlatform.Linux);

        var result = await collector.CollectAsync(CancellationToken.None);

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
            Write($"class/hwmon/hwmon0/fan{index}_input", "1000\n");

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

    private LinuxHwmonCollector CreateCollector() => new(
        Options.Create(new HardwareMetricsOptions { LinuxSysRoot = _temporary.Path }),
        new BoundedTextFileReader(),
        new TrustedPathResolver(StubHostPlatform.Linux),
        StubHostPlatform.Linux);

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_temporary.Path, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose() => _temporary.Dispose();
}
```

- [ ] **Step 2: Write failing source-storage tests**

```csharp
using ReachCommander.Application.Sources;
using ReachCommander.Domain.Sources;
using ReachCommander.Infrastructure.SystemMetrics;

namespace ReachCommander.UnitTests.SystemMetrics;

public sealed class SourceStorageCollectorTests
{
    [Fact]
    public async Task Collect_maps_safe_source_capacity_and_unavailable_sources()
    {
        ISourceCatalog catalog = new StubSourceCatalog([
            new SourceSnapshot("media", "Media", true, false, 1000, 750, 250, true, true),
            new SourceSnapshot("usb", "USB", false, true, null, null, null, false, false),
        ]);
        var result = await new SourceStorageCollector(catalog).CollectAsync(CancellationToken.None);

        Assert.Equal(75, result.Storage![0].UtilizationPercent);
        Assert.False(result.Storage[1].IsAvailable);
        Assert.DoesNotContain("path", string.Join('|', result.Storage.Select(item => item.ToString())), StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StubSourceCatalog(IReadOnlyList<SourceSnapshot> snapshots) : ISourceCatalog
    {
        public ValueTask<IReadOnlyList<SourceSnapshot>> GetSnapshotsAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(snapshots);
        public ValueTask<IReadOnlyList<SourceDefinition>> GetDefinitionsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask<SourceDefinition> GetRequiredAsync(string sourceId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
```

- [ ] **Step 3: Run focused tests and verify RED**

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter "FullyQualifiedName~LinuxHwmonCollectorTests|FullyQualifiedName~SourceStorageCollectorTests"
```

Expected: compilation fails because both collectors do not exist.

- [ ] **Step 4: Implement hwmon traversal and safe selection**

`LinuxHwmonCollector` behavior is exact:

It accepts `IHostPlatform` and `ITrustedPathResolver`, and returns Unsupported without filesystem access when `IsLinux` is false.

1. Enumerate at most 128 directories matching `class/hwmon/hwmon*` and 128 matching `class/thermal/thermal_zone*` under `LinuxSysRoot`, ordered ordinally.
2. Read only fixed `name`, `tempN_input`, `tempN_label`, `tempN_max`, `tempN_crit`, `tempN_alarm`, `tempN_fault`, `fanN_input`, `fanN_label`, `fanN_alarm`, and `fanN_fault` names for `N=1..128`.
3. Do not enumerate or return canonical target paths. Follow ordinary kernel class symlinks only through the mounted trusted sys root and reject any resolved target outside `LinuxSysRoot`.
4. CPU temperature candidates come from hwmon chip names `coretemp`, `k10temp`, `zenpower`, and `peci_cputemp`, or thermal-zone types containing `x86_pkg_temp`, `cpu`, `soc`, or `package`. Prefer hwmon labels containing Package, Tctl, Die, or CPU, then a labeled thermal-zone fallback, then the first finite candidate.
5. Convert millidegrees through `MetricNormalizer`; use hwmon `_max`/`_crit` or thermal-zone `trip_point_N_type` plus `trip_point_N_temp` for passive/hot and critical thresholds when valid. Read at most 32 fixed-index trip points per zone.
6. Map every available fan up to the family cap. A faulted input has null RPM; alarms and faults remain explicit.
7. When temperature/fan collection is disabled, omit that family and return success. Missing hwmon root or no sensors is `Unsupported`, not `Failed`.
8. Deduplicate hwmon and thermal-zone views whose `ITrustedPathResolver.GetCanonicalPath` values are equal before selecting a reading; canonical paths remain internal and are never serialized. Add a collector test with two fixture directories and a fake resolver mapping both to one identity, and assert that their duplicated fan appears once.
9. Permissions, I/O, invalid numbers, oversize input, or source-root escape produce a safe `Unavailable` or `Failed` status without raw paths.

Return a contribution containing a CPU record with only temperature fields populated and a deterministic fan ID `fan-{ordinal:D3}`. Task 6 merges this CPU contribution with LinuxProcCollector utilization.

- [ ] **Step 5: Implement safe configured-source storage mapping**

```csharp
using ReachCommander.Application.Sources;
using ReachCommander.Application.SystemMetrics;

namespace ReachCommander.Infrastructure.SystemMetrics;

internal sealed class SourceStorageCollector(ISourceCatalog sourceCatalog) : IHardwareMetricsCollector
{
    public string Name => "source-storage";
    public bool IsSupported => true;

    public async ValueTask<HardwareMetricsContribution> CollectAsync(CancellationToken cancellationToken)
    {
        var snapshots = await sourceCatalog.GetSnapshotsAsync(cancellationToken);
        var storage = snapshots.Select(source =>
        {
            var percent = source.TotalBytes is > 0 && source.UsedBytes is not null
                ? MetricNormalizer.Percent(100d * source.UsedBytes.Value / source.TotalBytes.Value)
                : null;
            return new StorageMetrics(
                source.Id,
                MetricNormalizer.Label(source.Name, source.Id),
                source.IsAvailable,
                MetricNormalizer.NonNegative(source.UsedBytes),
                MetricNormalizer.NonNegative(source.FreeBytes),
                MetricNormalizer.NonNegative(source.TotalBytes),
                percent);
        }).ToArray();

        return new HardwareMetricsContribution(
            new HardwareCollectorStatus(Name, HardwareCollectorState.Success, null),
            Storage: Array.AsReadOnly(storage));
    }
}
```

Catch expected catalog I/O/configuration failures at the collector boundary and return a safe failed status; never include a source root.

- [ ] **Step 6: Run focused and full backend tests**

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter "FullyQualifiedName~LinuxHwmonCollectorTests|FullyQualifiedName~SourceStorageCollectorTests"
dotnet test ReachCommander.slnx -c Release
```

Expected: Linux sensor/storage tests and the full solution pass.

- [ ] **Step 7: Commit sensor and storage collectors**

```powershell
git status --short
git add src/ReachCommander.Infrastructure/SystemMetrics/Linux/LinuxHwmonCollector.cs src/ReachCommander.Infrastructure/SystemMetrics/SourceStorageCollector.cs tests/ReachCommander.UnitTests/SystemMetrics/LinuxHwmonCollectorTests.cs tests/ReachCommander.UnitTests/SystemMetrics/SourceStorageCollectorTests.cs
git commit -m "feat: collect Linux sensors and source capacity"
```

---

### Task 4: NVIDIA, AMD, and Intel Linux GPU adapters

**Files:**

- Create: `src/ReachCommander.Infrastructure/SystemMetrics/Gpu/GpuVendor.cs`
- Create: `src/ReachCommander.Infrastructure/SystemMetrics/Gpu/LinuxDrmGpuCollector.cs`
- Create: `src/ReachCommander.Infrastructure/SystemMetrics/Gpu/NvidiaNvmlApi.cs`
- Create: `src/ReachCommander.Infrastructure/SystemMetrics/Gpu/NvidiaNvmlCollector.cs`
- Test: `tests/ReachCommander.UnitTests/SystemMetrics/LinuxGpuCollectorTests.cs`
- Test: `tests/ReachCommander.UnitTests/SystemMetrics/NativeNvidiaNvmlApiTests.cs`

**Interfaces:**

- Consumes: Task 1 `GpuMetrics`, normalization/options, and Task 2 collector contribution.
- Produces: internal `INvidiaNvmlApi`, `NvidiaDeviceSample`, `NvidiaNvmlCollector`, and `LinuxDrmGpuCollector` for Task 6.
- GPU IDs are snapshot-safe ordinals (`gpu-nvidia-001`, `gpu-amd-001`, `gpu-intel-001`), never PCI addresses.

- [ ] **Step 1: Write failing DRM and NVIDIA adapter tests**

```csharp
using Microsoft.Extensions.Options;
using ReachCommander.Application.SystemMetrics;
using ReachCommander.Infrastructure.SystemMetrics;
using ReachCommander.Infrastructure.SystemMetrics.Gpu;
using ReachCommander.Infrastructure.SystemMetrics.Linux;
using ReachCommander.UnitTests.Support;

namespace ReachCommander.UnitTests.SystemMetrics;

public sealed class LinuxGpuCollectorTests : IDisposable
{
    private readonly TemporaryDirectory _temporary = new();

    [Theory]
    [InlineData("0x1002", "AMD", "42", 42)]
    [InlineData("0x8086", "Intel", "17", 17)]
    public async Task Drm_collector_detects_vendor_and_maps_available_fields(
        string vendorId,
        string vendorName,
        string busy,
        double expectedBusy)
    {
        Write("class/drm/card0/device/vendor", vendorId);
        Write("class/drm/card0/device/gpu_busy_percent", busy);
        Write("class/drm/card0/device/mem_info_vram_used", "1048576");
        Write("class/drm/card0/device/mem_info_vram_total", "4194304");
        Write("class/drm/card0/device/hwmon/hwmon0/temp1_input", "61000");
        var collector = new LinuxDrmGpuCollector(
            Options.Create(new HardwareMetricsOptions { LinuxSysRoot = _temporary.Path }),
            new BoundedTextFileReader(),
            new TrustedPathResolver(StubHostPlatform.Linux),
            StubHostPlatform.Linux);

        var result = await collector.CollectAsync(CancellationToken.None);

        var gpu = Assert.Single(result.Gpus!);
        Assert.Equal(vendorName, gpu.Vendor);
        Assert.Equal(expectedBusy, gpu.UtilizationPercent);
        Assert.Equal(1_048_576, gpu.MemoryUsedBytes);
        Assert.Equal(4_194_304, gpu.MemoryTotalBytes);
        Assert.Equal(61, gpu.TemperatureCelsius);
        Assert.DoesNotContain("card0", gpu.Id, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Nvidia_collector_maps_safe_samples_and_isolates_unavailable_library()
    {
        INvidiaNvmlApi api = new StubNvmlApi([
            new NvidiaDeviceSample("GeForce RTX Test", 72, 2_000, 8_000, 64),
        ]);
        var collector = new NvidiaNvmlCollector(
            Options.Create(new HardwareMetricsOptions()), api, StubHostPlatform.Linux);

        var result = await collector.CollectAsync(CancellationToken.None);

        var gpu = Assert.Single(result.Gpus!);
        Assert.Equal("NVIDIA", gpu.Vendor);
        Assert.Equal("GeForce RTX Test", gpu.Name);
        Assert.Equal(72, gpu.UtilizationPercent);
        Assert.Equal(64, gpu.TemperatureCelsius);

        var unavailable = await new NvidiaNvmlCollector(
            Options.Create(new HardwareMetricsOptions()), new StubNvmlApi(null), StubHostPlatform.Linux)
            .CollectAsync(CancellationToken.None);
        Assert.Equal(HardwareCollectorState.Unsupported, unavailable.Status.State);
    }

    private sealed class StubNvmlApi(IReadOnlyList<NvidiaDeviceSample>? samples) : INvidiaNvmlApi
    {
        public IReadOnlyList<NvidiaDeviceSample>? TryReadDevices() => samples;
        public void Dispose() { }
    }

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_temporary.Path, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose() => _temporary.Dispose();
}
```

- [ ] **Step 2: Run GPU tests and verify RED**

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter FullyQualifiedName~LinuxGpuCollectorTests
```

Expected: compilation fails because GPU vendor, DRM, NVML boundary, and collectors do not exist.

- [ ] **Step 3: Implement safe GPU vendor identification**

```csharp
namespace ReachCommander.Infrastructure.SystemMetrics.Gpu;

internal enum GpuVendor { Nvidia, Amd, Intel }

internal static class GpuVendorIds
{
    public static GpuVendor? Parse(string value) => value.Trim().ToLowerInvariant() switch
    {
        "0x10de" => GpuVendor.Nvidia,
        "0x1002" => GpuVendor.Amd,
        "0x8086" => GpuVendor.Intel,
        _ => null,
    };

    public static string DisplayName(GpuVendor vendor) => vendor switch
    {
        GpuVendor.Nvidia => "NVIDIA",
        GpuVendor.Amd => "AMD",
        GpuVendor.Intel => "Intel",
        _ => throw new ArgumentOutOfRangeException(nameof(vendor)),
    };
}
```

Never return `cardN`, BDF/PCI data, `uevent`, serial, or device symlink targets.

- [ ] **Step 4: Implement AMD/Intel DRM and hwmon reading**

`LinuxDrmGpuCollector` must:

1. Activate only when injected `IHostPlatform.IsLinux` and `GpusEnabled` are true; an absent `class/drm` root is Unsupported.
2. Enumerate at most 16 ordinally sorted `card[0-9]+` directories, skipping connector names such as `card0-HDMI-A-1`.
3. Read `device/vendor`; emit only AMD `0x1002` and Intel `0x8086`. NVIDIA is owned by NVML and is skipped here.
4. Read optional fixed attributes `device/gpu_busy_percent`, `device/mem_info_vram_used`, and `device/mem_info_vram_total`.
5. Enumerate at most 16 `device/hwmon/hwmon*` directories and select the first valid `temp1_input`, with optional `_max`, `_crit`, `_alarm`, and `_fault`.
6. Give each adapter a safe name `AMD GPU {ordinal}` or `Intel GPU {ordinal}` and ID `gpu-amd-{ordinal:D3}` / `gpu-intel-{ordinal:D3}`.
7. Reject non-finite/out-of-range values, used memory greater than total, symlink escape outside `LinuxSysRoot`, oversized files, and more than 16 adapters.
8. Return Success with nullable fields when the adapter exists but a metric is unsupported. Return Unavailable only when an applicable readable adapter fails due to permissions/I/O.

Use only `BoundedTextFileReader`, invariant parsing, and `MetricNormalizer`. Do not call `amd-smi`, `rocm-smi`, `xpu-smi`, `intel_gpu_top`, or a shell.

- [ ] **Step 5: Define the high-level NVML boundary**

```csharp
namespace ReachCommander.Infrastructure.SystemMetrics.Gpu;

internal sealed record NvidiaDeviceSample(
    string Name,
    double? UtilizationPercent,
    long? MemoryUsedBytes,
    long? MemoryTotalBytes,
    double? TemperatureCelsius);

internal interface INvidiaNvmlApi : IDisposable
{
    IReadOnlyList<NvidiaDeviceSample>? TryReadDevices();
}
```

`NativeNvidiaNvmlApi` dynamically attempts `libnvidia-ml.so.1` then `libnvidia-ml.so` on Linux. Resolve exactly these exports:

```text
nvmlInit_v2
nvmlShutdown
nvmlDeviceGetCount_v2
nvmlDeviceGetHandleByIndex_v2
nvmlDeviceGetName
nvmlDeviceGetUtilizationRates
nvmlDeviceGetMemoryInfo
nvmlDeviceGetTemperature
```

Put `NativeLibrary.TryLoad`, `TryGetExport`, and `Free` behind an internal `INativeLibraryLoader` implemented by `RuntimeNativeLibraryLoader`, and inject it plus `IHostPlatform` into `NativeNvidiaNvmlApi`. Load and initialize lazily on the first Linux `TryReadDevices()` call; a Windows/non-Linux call returns null without attempting a library load. Resolve delegates with `CallingConvention.Cdecl`. Treat any missing required initialization/count/handle/name export or non-success return as unsupported/unavailable. Utilization, memory, and temperature exports are individually optional. Cap devices at 16, the UTF-8 name buffer at 96 bytes, and all numeric conversion with checked arithmetic. Hold one initialized handle per API instance and synchronize calls. Idempotent disposal obtains the same gate within the configured collector timeout, calls `nvmlShutdown` once, and frees once; if an uninterruptible native call still owns the gate, log only `native_dispose_deferred` and leave the process-exit cleanup to the OS rather than freeing code beneath an active call or exceeding the shutdown budget.

In `NativeNvidiaNvmlApiTests`, use a fake loader whose exports are rooted delegates (to prevent garbage collection) and prove: the first library name can fail before the second succeeds; two fake devices are mapped; a missing optional temperature export leaves temperature null; an initialization error returns null without raw text; and repeated `Dispose()` invokes shutdown/free exactly once. These tests run with `StubHostPlatform.Linux` and never load a real driver.

- [ ] **Step 6: Implement the NVIDIA collector**

`NvidiaNvmlCollector` returns Unsupported when disabled, injected `IHostPlatform.IsLinux` is false, or `TryReadDevices()` returns null. Otherwise map up to 16 samples in returned order to `GpuMetrics` using:

```csharp
new GpuMetrics(
    $"gpu-nvidia-{index + 1:D3}",
    "NVIDIA",
    MetricNormalizer.Label(sample.Name, $"NVIDIA GPU {index + 1}"),
    MetricNormalizer.Percent(sample.UtilizationPercent),
    MetricNormalizer.NonNegative(sample.MemoryUsedBytes),
    MetricNormalizer.NonNegative(sample.MemoryTotalBytes),
    MetricNormalizer.Celsius(sample.TemperatureCelsius),
    WarningTemperatureCelsius: null,
    CriticalTemperatureCelsius: null,
    Alarm: false,
    Fault: false)
```

If used memory exceeds total, set used to null. Convert expected native failures to stable codes; never return native error text.

- [ ] **Step 7: Run focused/full tests and commit**

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter "FullyQualifiedName~LinuxGpuCollectorTests|FullyQualifiedName~NativeNvidiaNvmlApiTests"
dotnet test ReachCommander.slnx -c Release
git status --short
git add src/ReachCommander.Infrastructure/SystemMetrics/Gpu tests/ReachCommander.UnitTests/SystemMetrics/LinuxGpuCollectorTests.cs tests/ReachCommander.UnitTests/SystemMetrics/NativeNvidiaNvmlApiTests.cs
git commit -m "feat: collect Linux GPU metrics"
```

Expected: GPU fixture tests and all backend tests pass; no external GPU tool is required.

---

### Task 5: Native Windows collector through LibreHardwareMonitor

**Files:**

- Modify: `src/ReachCommander.Infrastructure/ReachCommander.Infrastructure.csproj`
- Create: `src/ReachCommander.Infrastructure/SystemMetrics/Windows/LibreHardwareMonitorAdapter.cs`
- Create: `src/ReachCommander.Infrastructure/SystemMetrics/Windows/WindowsHardwareCollector.cs`
- Test: `tests/ReachCommander.UnitTests/SystemMetrics/LibreHardwareMonitorAdapterTests.cs`
- Test: `tests/ReachCommander.UnitTests/SystemMetrics/WindowsHardwareCollectorTests.cs`

**Interfaces:**

- Consumes: Task 1 public metrics/options/normalization and Task 2 collector contribution.
- Produces: internal `IWindowsSensorSource`, normalized `WindowsSensorReading`, `LibreHardwareMonitorAdapter`, and `WindowsHardwareCollector` for Task 6.
- Uses: stable NuGet package `LibreHardwareMonitorLib` `0.9.6` only.

- [ ] **Step 1: Add the pinned package and write failing mapping tests**

Add:

```xml
<ItemGroup>
  <PackageReference Include="LibreHardwareMonitorLib" Version="0.9.6" />
</ItemGroup>
```

Write collector mapping tests that do not instantiate the real library:

```csharp
using Microsoft.Extensions.Options;
using ReachCommander.Infrastructure.SystemMetrics;
using ReachCommander.Infrastructure.SystemMetrics.Windows;
using ReachCommander.UnitTests.Support;

namespace ReachCommander.UnitTests.SystemMetrics;

public sealed class WindowsHardwareCollectorTests
{
    [Fact]
    public async Task Collect_maps_cpu_memory_gpu_fan_network_and_uptime_from_safe_nodes()
    {
        using IWindowsSensorSource source = new StubWindowsSensorSource([
            new(WindowsDeviceKind.Cpu, "CPU", WindowsSensorKind.UtilizationPercent, "CPU Total", 25),
            new(WindowsDeviceKind.Cpu, "CPU", WindowsSensorKind.TemperatureCelsius, "CPU Package", 58),
            new(WindowsDeviceKind.Memory, "Memory", WindowsSensorKind.MemoryUsedBytes, "Memory Used", 6_000),
            new(WindowsDeviceKind.Memory, "Memory", WindowsSensorKind.MemoryAvailableBytes, "Memory Available", 10_000),
            new(WindowsDeviceKind.GpuNvidia, "RTX Test", WindowsSensorKind.UtilizationPercent, "GPU Core", 72),
            new(WindowsDeviceKind.GpuNvidia, "RTX Test", WindowsSensorKind.MemoryUsedBytes, "GPU Memory Used", 2_000),
            new(WindowsDeviceKind.GpuNvidia, "RTX Test", WindowsSensorKind.MemoryTotalBytes, "GPU Memory Total", 8_000),
            new(WindowsDeviceKind.GpuNvidia, "RTX Test", WindowsSensorKind.TemperatureCelsius, "GPU Core", 64),
            new(WindowsDeviceKind.Motherboard, "Board", WindowsSensorKind.FanRpm, "CPU Fan", 1400),
            new(WindowsDeviceKind.Network, "Ethernet", WindowsSensorKind.ReceiveBytesPerSecond, "Download", 1000),
            new(WindowsDeviceKind.Network, "Ethernet", WindowsSensorKind.TransmitBytesPerSecond, "Upload", 500),
        ], uptimeSeconds: 3600);
        var collector = new WindowsHardwareCollector(
            Options.Create(new HardwareMetricsOptions()), source, StubHostPlatform.Windows);

        var result = await collector.CollectAsync(CancellationToken.None);

        Assert.Equal(25, result.Cpu?.UtilizationPercent);
        Assert.Equal(58, result.Cpu?.TemperatureCelsius);
        Assert.Equal(16_000, result.Memory?.TotalBytes);
        Assert.Equal(37.5, result.Memory?.UtilizationPercent);
        Assert.Equal(72, Assert.Single(result.Gpus!).UtilizationPercent);
        Assert.Equal(1400, Assert.Single(result.Fans!).RevolutionsPerMinute);
        Assert.Equal(1000, result.Network?.ReceiveBytesPerSecond);
        Assert.Equal(3600, result.HostUptimeSeconds);
    }

    [Fact]
    public async Task Collect_sanitizes_labels_and_returns_partial_values_when_sensors_are_missing()
    {
        using IWindowsSensorSource source = new StubWindowsSensorSource([
            new(WindowsDeviceKind.GpuAmd, " GPU\0 Test ", WindowsSensorKind.TemperatureCelsius, "Core", double.NaN),
        ], uptimeSeconds: 1);

        var result = await new WindowsHardwareCollector(
                Options.Create(new HardwareMetricsOptions()), source, StubHostPlatform.Windows)
            .CollectAsync(CancellationToken.None);

        var gpu = Assert.Single(result.Gpus!);
        Assert.Equal("GPU Test", gpu.Name);
        Assert.Null(gpu.TemperatureCelsius);
    }

    private sealed class StubWindowsSensorSource(
        IReadOnlyList<WindowsSensorReading> readings,
        long uptimeSeconds) : IWindowsSensorSource
    {
        public IReadOnlyList<WindowsSensorReading> Read() => readings;
        public long GetUptimeSeconds() => uptimeSeconds;
        public void Dispose() { }
    }
}
```

- [ ] **Step 2: Run the Windows collector tests and verify RED**

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter FullyQualifiedName~WindowsHardwareCollectorTests
```

Expected: compilation fails because the Windows adapter boundary and collector do not exist.

- [ ] **Step 3: Define the stable adapter model**

```csharp
namespace ReachCommander.Infrastructure.SystemMetrics.Windows;

internal enum WindowsDeviceKind
{
    Cpu, Memory, Motherboard, Controller, Network, GpuNvidia, GpuAmd, GpuIntel
}

internal enum WindowsSensorKind
{
    UtilizationPercent,
    TemperatureCelsius,
    MemoryUsedBytes,
    MemoryAvailableBytes,
    MemoryTotalBytes,
    FanRpm,
    ReceiveBytesPerSecond,
    TransmitBytesPerSecond,
}

internal sealed record WindowsSensorReading(
    WindowsDeviceKind DeviceKind,
    string DeviceName,
    WindowsSensorKind SensorKind,
    string SensorName,
    double Value);

internal interface IWindowsSensorSource : IDisposable
{
    IReadOnlyList<WindowsSensorReading> Read();
    long GetUptimeSeconds();
}
```

In `LibreHardwareMonitorAdapter.cs`, also define `WindowsRawSensorKind` (`Load`, `Temperature`, `Data`, `SmallData`, `Fan`, `Throughput`), `WindowsRawSensor(WindowsRawSensorKind Kind, string Name, double? Value)`, `WindowsRawDevice(WindowsDeviceKind Kind, string Name, string InternalIdentifier, IReadOnlyList<WindowsRawSensor> Sensors)`, and an `ILibreHardwareSession` boundary with `Open()`, `Update()`, `ReadDevices()`, and `Dispose()`. The production session is the only type that references LibreHardwareMonitor's `Computer`, `IHardware`, and `ISensor`; the adapter consumes raw immutable nodes and never copies `InternalIdentifier` into a reading. This makes both the third-party traversal and mapping testable without opening hardware.

- [ ] **Step 4: Implement the LibreHardwareMonitor adapter**

`LibreHardwareMonitorAdapter` owns one `ILibreHardwareSession`; the production session owns one `Computer` configured with CPU, memory, motherboard, controller, network, and GPU flags. `Read()` lazily opens the session, calls `Update()` once, reads the immutable tree once, and maps only approved device/sensor combinations.

Exact rules:

- CPU Load sensor named `CPU Total` or the first aggregate CPU Load becomes utilization.
- CPU Temperature named `CPU Package`, `Package`, `Tctl`, or `Core Average` becomes package temperature in that priority.
- Memory Data sensors `Memory Used`/`Memory Available` convert GiB to bytes with `1024^3`; derive total later. Memory Load is a fallback utilization only when byte sensors are absent.
- GPU hardware types map to NVIDIA/AMD/Intel. Load `GPU Core`/`D3D 3D` becomes utilization; SmallData/Data memory sensors normalize MiB/GiB to bytes in the adapter; Temperature `GPU Core`/`GPU Hot Spot` becomes temperature.
- Fan sensors under motherboard/controller/GPU map to RPM. Keep at most 128.
- Network Throughput sensors with Download/Receive and Upload/Transmit labels map directly to bytes per second and aggregate later.
- `Environment.TickCount64 / 1000` supplies non-negative uptime.

The adapter never exposes `Identifier`, serials, PCI data, or raw exceptions. `Open()` is lazy and synchronized; idempotent disposal obtains the same gate within the configured collector timeout and disposes once. If an uninterruptible read still owns the gate, log only `windows_dispose_deferred` and let process exit reclaim it instead of blocking beyond the shutdown budget. If injected `IHostPlatform.IsWindows` is false, constructing the adapter is allowed but `Read()` returns an empty list without opening hardware.

Create `LibreHardwareMonitorAdapterTests.cs` with a fake session and assert: two `Read()` calls open once/update twice; CPU/GPU/memory/fan/network raw nodes map with the specified unit conversions and priorities; identifiers present only on fake raw internals never appear in `WindowsSensorReading`; non-Windows reads never open; and two `Dispose()` calls dispose the session exactly once.

- [ ] **Step 5: Implement Windows aggregation**

`WindowsHardwareCollector` groups normalized readings by device name and kind. It emits:

- one CPU from the selected usage/temperature readings;
- memory when either a valid used/available byte pair or a valid Memory Load exists; derive total/percentage from bytes when possible, otherwise retain null byte fields and the normalized load percentage;
- up to 16 GPUs with safe ordinal IDs/vendor labels;
- up to 128 fans with safe ordinal IDs;
- one network metric by checked sum of each available non-negative receive/transmit direction, retaining null for a direction with no reading;
- source uptime from the adapter.

When disabled or injected `IHostPlatform.IsWindows` is false, return Unsupported. Expected permissions/native/library errors return Unavailable `windows_sensors_unavailable`. Never return `Exception.Message`. Missing individual readings remain null and do not fail the collector.

- [ ] **Step 6: Run tests, inspect dependency, and commit**

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter "FullyQualifiedName~WindowsHardwareCollectorTests|FullyQualifiedName~LibreHardwareMonitorAdapterTests"
dotnet list src/ReachCommander.Infrastructure/ReachCommander.Infrastructure.csproj package --include-transitive
dotnet test ReachCommander.slnx -c Release
git status --short
git add src/ReachCommander.Infrastructure/ReachCommander.Infrastructure.csproj src/ReachCommander.Infrastructure/SystemMetrics/Windows tests/ReachCommander.UnitTests/SystemMetrics/LibreHardwareMonitorAdapterTests.cs tests/ReachCommander.UnitTests/SystemMetrics/WindowsHardwareCollectorTests.cs
git commit -m "feat: collect native Windows hardware metrics"
```

Expected: package graph contains pinned LibreHardwareMonitorLib 0.9.6, Windows mapping tests pass without hardware, and all backend tests pass.

---

### Task 6: Five-second sampler, bounded collector execution, cache, and registration

**Files:**

- Create: `src/ReachCommander.Infrastructure/SystemMetrics/HardwareMetricsSnapshotCache.cs`
- Create: `src/ReachCommander.Infrastructure/SystemMetrics/HardwareMetricsSampler.cs`
- Modify: `src/ReachCommander.Infrastructure/DependencyInjection.cs`
- Test: `tests/ReachCommander.UnitTests/SystemMetrics/BoundedHardwareCollectorRunnerTests.cs`
- Test: `tests/ReachCommander.UnitTests/SystemMetrics/HardwareMetricsSamplerTests.cs`

**Interfaces:**

- Consumes: all Task 1-5 collectors and metrics contracts.
- Produces: singleton `IHardwareMetricsSnapshotProvider` and hosted `HardwareMetricsSampler` for Task 7.
- Produces: internal `HardwareMetricsSampler.SampleOnceAsync(CancellationToken)` for deterministic unit tests.

- [ ] **Step 1: Write failing merge, stale, disabled, and timeout tests**

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReachCommander.Application.SystemMetrics;
using ReachCommander.Infrastructure.SystemMetrics;

namespace ReachCommander.UnitTests.SystemMetrics;

public sealed class HardwareMetricsSamplerTests
{
    [Fact]
    public async Task Sample_merges_complementary_cpu_fields_and_marks_failed_applicable_collector_partial()
    {
        var clock = new ManualMetricsTimeProvider(new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero));
        var cache = new HardwareMetricsSnapshotCache(
            Options.Create(new HardwareMetricsOptions()), clock);
        IHardwareMetricsCollector[] collectors = [
            Collector("linux-proc", new HardwareMetricsContribution(
                Status("linux-proc"), 99,
                new CpuMetrics(25, null, null, null, false, false),
                new MemoryMetrics(60, 40, 100, 60),
                Network: new NetworkMetrics(1000, 500))),
            Collector("linux-hwmon", new HardwareMetricsContribution(
                Status("linux-hwmon"),
                Cpu: new CpuMetrics(null, 55, 90, 100, false, false))),
            Collector("gpu", new HardwareMetricsContribution(
                new HardwareCollectorStatus("gpu", HardwareCollectorState.Unavailable, "gpu_access_denied"))),
        ];
        var sampler = CreateSampler(collectors, cache, clock, new ImmediateCollectorRunner());

        await sampler.SampleOnceAsync(CancellationToken.None);
        var snapshot = cache.GetCurrent();

        Assert.Equal(HardwareMetricsState.Partial, snapshot.State);
        Assert.Equal(25, snapshot.Cpu?.UtilizationPercent);
        Assert.Equal(55, snapshot.Cpu?.TemperatureCelsius);
        Assert.Equal(99, snapshot.HostUptimeSeconds);
    }

    [Fact]
    public void Cache_marks_last_good_snapshot_stale_after_fifteen_seconds()
    {
        var clock = new ManualMetricsTimeProvider(new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero));
        var cache = new HardwareMetricsSnapshotCache(Options.Create(new HardwareMetricsOptions()), clock);
        cache.Set(Snapshot(clock.GetUtcNow()));

        clock.Advance(TimeSpan.FromSeconds(16));

        Assert.Equal(HardwareMetricsState.Stale, cache.GetCurrent().State);
    }

    [Fact]
    public async Task Disabled_sampler_publishes_disabled_snapshot_without_invoking_collectors()
    {
        var clock = new ManualMetricsTimeProvider(DateTimeOffset.UtcNow);
        var cache = new HardwareMetricsSnapshotCache(
            Options.Create(new HardwareMetricsOptions { Enabled = false }), clock);
        var collector = new CountingCollector();
        var sampler = CreateSampler([collector], cache, clock, new ImmediateCollectorRunner(), enabled: false);

        await sampler.SampleOnceAsync(CancellationToken.None);

        Assert.Equal(0, collector.CallCount);
        Assert.Equal(HardwareMetricsState.Disabled, cache.GetCurrent().State);
    }

    [Fact]
    public async Task Timeout_does_not_remove_another_collectors_data()
    {
        var clock = new ManualMetricsTimeProvider(DateTimeOffset.UtcNow);
        var cache = new HardwareMetricsSnapshotCache(Options.Create(new HardwareMetricsOptions()), clock);
        var good = Collector("good", new HardwareMetricsContribution(
            Status("good"), Cpu: new CpuMetrics(10, null, null, null, false, false)));
        var slow = Collector("slow", new HardwareMetricsContribution(Status("slow")));
        var sampler = CreateSampler([good, slow], cache, clock, new FakeTimeoutCollectorRunner("slow"));

        await sampler.SampleOnceAsync(CancellationToken.None);

        Assert.Equal(10, cache.GetCurrent().Cpu?.UtilizationPercent);
        Assert.Contains(cache.GetCurrent().Collectors,
            status => status.Collector == "slow" && status.State == HardwareCollectorState.Timeout);
    }

    private static HardwareMetricsSampler CreateSampler(
        IEnumerable<IHardwareMetricsCollector> collectors,
        HardwareMetricsSnapshotCache cache,
        TimeProvider clock,
        IHardwareCollectorRunner runner,
        bool enabled = true,
        IHardwareMetricsDelay? delay = null) => new(
            collectors,
            cache,
            runner,
            delay ?? new BlockingMetricsDelay(),
            Options.Create(new HardwareMetricsOptions { Enabled = enabled }),
            clock,
            NullLogger<HardwareMetricsSampler>.Instance);

    private static IHardwareMetricsCollector Collector(string name, HardwareMetricsContribution contribution) =>
        new FixedCollector(name, contribution);
    private static HardwareCollectorStatus Status(string name) =>
        new(name, HardwareCollectorState.Success, null);
    private static HardwareMetricsSnapshot Snapshot(DateTimeOffset sampledAt) => new(
        sampledAt, HardwareMetricsState.Healthy, 1, null, null, [], [], [], null, []);

    private sealed class FixedCollector(string name, HardwareMetricsContribution contribution) : IHardwareMetricsCollector
    {
        public string Name => name;
        public bool IsSupported => true;
        public ValueTask<HardwareMetricsContribution> CollectAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(contribution);
    }

    private sealed class CountingCollector : IHardwareMetricsCollector
    {
        public int CallCount { get; private set; }
        public string Name => "counting";
        public bool IsSupported => true;
        public ValueTask<HardwareMetricsContribution> CollectAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            return ValueTask.FromResult(new HardwareMetricsContribution(Status(Name)));
        }
    }

    private sealed class BlockingMetricsDelay : IHardwareMetricsDelay
    {
        public Task DelayAsync(TimeSpan interval, TimeProvider timeProvider, CancellationToken cancellationToken) =>
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}
```

`ImmediateCollectorRunner` directly awaits a collector. `FakeTimeoutCollectorRunner` returns a Timeout contribution with code `collector_timeout` only for the configured collector name and delegates other calls. Keep both fakes in this test file. Add `GetCurrent` before first `Set` asserting `HardwareMetricsNotReadyException`.

Add three more deterministic sampler facts:

- `Transient_failure_preserves_family_then_recovery_replaces_it`: sample mutable required collectors at CPU 10, switch CPU/RAM to Failed and confirm CPU 10 remains with Partial state and unchanged `SampledAt`, advance 16 seconds and confirm Stale, then switch to Success CPU 20 and confirm Healthy/20 with a new timestamp.
- `Concurrent_sample_request_is_skipped`: hold the first collector task, invoke `SampleOnceAsync` again, and confirm its collector call count remains one.
- `Hosted_loop_samples_immediately_then_requests_exact_five_second_delay`: inject a fake `IHardwareMetricsDelay`, start the hosted service, confirm one collection before releasing a delay, assert the requested delay is five seconds, release once, and confirm exactly one additional collection before stopping.

Create `BoundedHardwareCollectorRunnerTests.cs`. A gated collector plus `TimeSpan.Zero` must prove two timed-out runner calls start the collector only once; after the gate completes and the finished task is removed, the next runner call starts it a second time. Add a late-fault case and assert it is observed/logged without escaping on the next sample, plus caller-cancellation propagation before any work starts.

- [ ] **Step 2: Run sampler tests and verify RED**

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter FullyQualifiedName~HardwareMetricsSamplerTests
```

Expected: compilation fails because cache, sampler, and runner do not exist.

- [ ] **Step 3: Implement the atomic effective-stale cache**

```csharp
using Microsoft.Extensions.Options;
using ReachCommander.Application.SystemMetrics;

namespace ReachCommander.Infrastructure.SystemMetrics;

internal sealed class HardwareMetricsSnapshotCache(
    IOptions<HardwareMetricsOptions> options,
    TimeProvider timeProvider) : IHardwareMetricsSnapshotProvider
{
    private HardwareMetricsSnapshot? _snapshot;

    public void Set(HardwareMetricsSnapshot snapshot) =>
        Interlocked.Exchange(ref _snapshot, snapshot);

    public HardwareMetricsSnapshot GetCurrent()
    {
        var snapshot = Volatile.Read(ref _snapshot)
            ?? throw new HardwareMetricsNotReadyException();
        if (snapshot.State == HardwareMetricsState.Disabled)
            return snapshot;
        return timeProvider.GetUtcNow() - snapshot.SampledAt >
            TimeSpan.FromSeconds(options.Value.StaleAfterSeconds)
                ? snapshot with { State = HardwareMetricsState.Stale }
                : snapshot;
    }
}
```

- [ ] **Step 4: Implement bounded, non-overlapping collector execution**

Define:

```csharp
internal interface IHardwareCollectorRunner
{
    ValueTask<HardwareMetricsContribution> RunAsync(
        IHardwareMetricsCollector collector,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal interface IHardwareMetricsDelay
{
    Task DelayAsync(TimeSpan interval, TimeProvider timeProvider, CancellationToken cancellationToken);
}
```

`BoundedHardwareCollectorRunner` keeps one `ConcurrentDictionary<IHardwareMetricsCollector, Task<HardwareMetricsContribution>>` constructed with `ReferenceEqualityComparer.Instance`, so each collector has at most one in-flight task. After `cancellationToken.ThrowIfCancellationRequested()`, it starts work with `Task.Run(async () => await collector.CollectAsync(cancellationToken), CancellationToken.None)`, awaits `WaitAsync(timeout, cancellationToken)`, and on timeout returns a Timeout contribution with `collector_timeout` without canceling or starting another call until the old task completes. A continuation observes and logs late faults, removes that exact key/value pair, and discards its value. Expected permission/I/O/format/native failures map to safe Failed/Unavailable codes. Caller cancellation reaches cooperative managed collectors; timeout of one collector does not cancel peers.

The test runners remain test-only and provide deterministic timing without wall-clock sleeps.

`HardwareMetricsDelay` implements the production delay as `Task.Delay(interval, timeProvider, cancellationToken)`. The fake delay used above captures the interval in a channel/TCS and advances only when the test releases it.

- [ ] **Step 5: Implement one-shot aggregation and hosted cadence**

`HardwareMetricsSampler : BackgroundService` must:

1. Publish a Disabled snapshot immediately without calling collectors when disabled.
2. Call `SampleOnceAsync` immediately from `ExecuteAsync`, then delay exactly the configured five seconds through `IHardwareMetricsDelay.DelayAsync(interval, timeProvider, stoppingToken)`; the production implementation delegates to `Task.Delay(interval, timeProvider, stoppingToken)`.
3. Filter `IsSupported == false` collectors into Unsupported statuses without invoking them.
4. Run supported collectors concurrently through `IHardwareCollectorRunner` and await all safe runner results.
5. Merge CPU utilization from the first non-null value and CPU temperature/threshold/alarm/fault from the first non-null hwmon/Windows value.
6. Select the first non-null valid memory, uptime, and network contribution; concatenate bounded storage, GPU, fan, and collector lists in registration order.
7. Deduplicate GPUs by safe ID and fans by `(Name, RPM)`, cap all lists through `MetricNormalizer`, and expose read-only arrays.
8. Treat Unsupported collectors as neutral. Set Healthy when every applicable collector succeeded and required base CPU/RAM/storage contributions exist; set Partial when base data exists but an applicable collector returned Unavailable/Timeout/Failed or a required base field is missing.
9. Keep the previous usable data for a family when a transient collector failure occurs, but replace current collector statuses. Advance `SampledAt` only when the current aggregation contains successful required CPU, RAM, and storage contributions. The first enabled attempt publishes its attempt time even if partial; later attempts missing required base data retain the previous `SampledAt`, allowing the cache read path to become Stale after 15 seconds. Recovery advances it again.
10. Log snapshot correlation GUID, collector name/state, elapsed milliseconds, and stable code only.

Expose internal `SampleOnceAsync` for tests. Use a `SemaphoreSlim(1,1)` with non-blocking entry so a manual/concurrent call never overlaps aggregation.

- [ ] **Step 6: Register validated options, collectors, runner, cache, and hosted service**

In `DependencyInjection.AddReachCommanderInfrastructure` add:

```csharp
services.AddOptions<HardwareMetricsOptions>()
    .Bind(configuration.GetSection(HardwareMetricsOptions.SectionName))
    .ValidateOnStart();
services.AddSingleton<IValidateOptions<HardwareMetricsOptions>, HardwareMetricsOptionsValidator>();
services.AddSingleton<TimeProvider>(TimeProvider.System);
services.AddSingleton<IHostPlatform, RuntimeHostPlatform>();
services.AddSingleton<BoundedTextFileReader>();
services.AddSingleton<ITrustedPathResolver, TrustedPathResolver>();
services.AddSingleton<IHardwareMetricsCollector, LinuxProcCollector>();
services.AddSingleton<IHardwareMetricsCollector, LinuxHwmonCollector>();
services.AddSingleton<IHardwareMetricsCollector, SourceStorageCollector>();
services.AddSingleton<INativeLibraryLoader, RuntimeNativeLibraryLoader>();
services.AddSingleton<INvidiaNvmlApi, NativeNvidiaNvmlApi>();
services.AddSingleton<IHardwareMetricsCollector, NvidiaNvmlCollector>();
services.AddSingleton<IHardwareMetricsCollector, LinuxDrmGpuCollector>();
services.AddSingleton<IWindowsSensorSource, LibreHardwareMonitorAdapter>();
services.AddSingleton<IHardwareMetricsCollector, WindowsHardwareCollector>();
services.AddSingleton<IHardwareCollectorRunner, BoundedHardwareCollectorRunner>();
services.AddSingleton<IHardwareMetricsDelay, HardwareMetricsDelay>();
services.AddSingleton<HardwareMetricsSnapshotCache>();
services.AddSingleton<IHardwareMetricsSnapshotProvider>(provider =>
    provider.GetRequiredService<HardwareMetricsSnapshotCache>());
services.AddHostedService<HardwareMetricsSampler>();
```

If the Multi-Rename plan already registered `TimeProvider`, keep exactly one registration. Singleton collectors must be concurrency-safe and disposable native adapters must be disposed by DI.

- [ ] **Step 7: Run focused/full tests and commit**

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter "FullyQualifiedName~HardwareMetricsSamplerTests|FullyQualifiedName~BoundedHardwareCollectorRunnerTests"
dotnet test ReachCommander.slnx -c Release
git status --short
git add src/ReachCommander.Infrastructure/SystemMetrics/HardwareMetricsSnapshotCache.cs src/ReachCommander.Infrastructure/SystemMetrics/HardwareMetricsSampler.cs src/ReachCommander.Infrastructure/DependencyInjection.cs tests/ReachCommander.UnitTests/SystemMetrics/BoundedHardwareCollectorRunnerTests.cs tests/ReachCommander.UnitTests/SystemMetrics/HardwareMetricsSamplerTests.cs
git commit -m "feat: sample and cache hardware metrics"
```

Expected: deterministic merge/cache tests and the full backend suite pass.

---

### Task 7: Read-only system-metrics API and safe integration coverage

**Files:**

- Create: `src/ReachCommander.Api/Contracts/SystemMetrics/SystemMetricsDto.cs`
- Create: `src/ReachCommander.Api/Controllers/SystemMetricsController.cs`
- Modify: `src/ReachCommander.Api/Errors/FileAccessExceptionHandler.cs`
- Create: `tests/ReachCommander.IntegrationTests/SystemMetricsApiTests.cs`
- Modify: `tests/ReachCommander.IntegrationTests/ReachCommanderApiFactory.cs`

**Interfaces:**

- Consumes: Task 1 `IHardwareMetricsSnapshotProvider` and public records.
- Produces: `GET /api/system-metrics` and camel-case DTOs consumed by Task 8.
- Produces: 503 Problem Details code `metrics_not_ready` only before the first enabled sample.

- [ ] **Step 1: Write failing API integration tests**

```csharp
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
```

- [ ] **Step 2: Run integration tests and verify RED**

```powershell
dotnet test tests/ReachCommander.IntegrationTests/ReachCommander.IntegrationTests.csproj -c Release --filter FullyQualifiedName~SystemMetricsApiTests
```

Expected: requests return 404 because the controller and DTO do not exist.

- [ ] **Step 3: Add exact safe response DTOs**

Create DTO records mirroring every public field with no internal additions:

```csharp
public sealed record SystemMetricsDto(
    DateTimeOffset SampledAt,
    HardwareMetricsState State,
    long? HostUptimeSeconds,
    CpuMetricsDto? Cpu,
    MemoryMetricsDto? Memory,
    IReadOnlyList<StorageMetricsDto> Storage,
    IReadOnlyList<GpuMetricsDto> Gpus,
    IReadOnlyList<FanMetricsDto> Fans,
    NetworkMetricsDto? Network,
    IReadOnlyList<HardwareCollectorStatusDto> Collectors);

public sealed record CpuMetricsDto(double? UtilizationPercent, double? TemperatureCelsius, double? WarningTemperatureCelsius, double? CriticalTemperatureCelsius, bool Alarm, bool Fault);
public sealed record MemoryMetricsDto(long? UsedBytes, long? AvailableBytes, long? TotalBytes, double? UtilizationPercent);
public sealed record StorageMetricsDto(string SourceId, string Name, bool IsAvailable, long? UsedBytes, long? FreeBytes, long? TotalBytes, double? UtilizationPercent);
public sealed record GpuMetricsDto(string Id, string Vendor, string Name, double? UtilizationPercent, long? MemoryUsedBytes, long? MemoryTotalBytes, double? TemperatureCelsius, double? WarningTemperatureCelsius, double? CriticalTemperatureCelsius, bool Alarm, bool Fault);
public sealed record FanMetricsDto(string Id, string Name, int? RevolutionsPerMinute, bool Alarm, bool Fault);
public sealed record NetworkMetricsDto(long? ReceiveBytesPerSecond, long? TransmitBytesPerSecond);
public sealed record HardwareCollectorStatusDto(string Collector, HardwareCollectorState State, string? Code);
```

Add `SystemMetricsDto.FromSnapshot` that creates new arrays and maps fields explicitly. Do not serialize application records directly.

- [ ] **Step 4: Add the thin GET controller and exception mapping**

```csharp
using Microsoft.AspNetCore.Mvc;
using ReachCommander.Api.Contracts.SystemMetrics;
using ReachCommander.Application.SystemMetrics;

namespace ReachCommander.Api.Controllers;

[ApiController]
[Route("api/system-metrics")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class SystemMetricsController(
    IHardwareMetricsSnapshotProvider snapshotProvider) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<SystemMetricsDto>(StatusCodes.Status200OK)]
    public ActionResult<SystemMetricsDto> Get() =>
        Ok(SystemMetricsDto.FromSnapshot(snapshotProvider.GetCurrent()));
}
```

Map `HardwareMetricsNotReadyException` to 503, title `Hardware metrics not ready`, code `metrics_not_ready`, and fixed detail `Hardware metrics have not completed their first sample.` in `FileAccessExceptionHandler`. Keep unexpected errors sanitized.

- [ ] **Step 5: Replace the provider safely in integration tests**

In `ReachCommanderApiFactory`, create one private `TestHardwareMetricsSnapshotProvider` field. Override `ConfigureWebHost` and add an in-memory `HardwareMetrics:Enabled=false` setting before services are built so the real hosted sampler never probes the test runner's hardware. In `ConfigureTestServices`, call `services.RemoveAll<IHardwareMetricsSnapshotProvider>()` and register that exact field as the singleton provider. The test provider has synchronized `Set(snapshot)` and `SetNotReady()` methods; `GetCurrent()` returns the current snapshot or throws `HardwareMetricsNotReadyException`. Expose `SetHardwareSnapshot` and `SetHardwareNotReady` factory methods used above. Each metrics test sets its state before `CreateClient`; other API tests remain independent because they never read the provider.

Use this configuration shape:

```csharp
builder.ConfigureAppConfiguration((_, configuration) =>
    configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["HardwareMetrics:Enabled"] = "false",
    }));
```

- [ ] **Step 6: Run integration/full tests and commit**

```powershell
dotnet test tests/ReachCommander.IntegrationTests/ReachCommander.IntegrationTests.csproj -c Release --filter FullyQualifiedName~SystemMetricsApiTests
dotnet test ReachCommander.slnx -c Release
git status --short
git add src/ReachCommander.Api/Contracts/SystemMetrics/SystemMetricsDto.cs src/ReachCommander.Api/Controllers/SystemMetricsController.cs src/ReachCommander.Api/Errors/FileAccessExceptionHandler.cs tests/ReachCommander.IntegrationTests/SystemMetricsApiTests.cs tests/ReachCommander.IntegrationTests/ReachCommanderApiFactory.cs
git commit -m "feat: expose safe system metrics API"
```

Expected: API tests return safe JSON and all backend tests pass.

---

### Task 8: Angular transport, formatting, and five-second metrics state

**Files:**

- Modify: `client/reach-commander-ui/src/app/core/api/api.models.ts`
- Modify: `client/reach-commander-ui/src/app/core/api/reach-commander-api.ts`
- Modify: `client/reach-commander-ui/src/app/core/api/reach-commander-api.spec.ts`
- Create: `client/reach-commander-ui/src/app/core/state/system-metrics-store.ts`
- Test: `client/reach-commander-ui/src/app/core/state/system-metrics-store.spec.ts`
- Create: `client/reach-commander-ui/src/app/shared/pipes/byte-rate.pipe.ts`
- Test: `client/reach-commander-ui/src/app/shared/pipes/byte-rate.pipe.spec.ts`
- Modify: `client/reach-commander-ui/src/app/app.spec.ts`
- Modify: `client/reach-commander-ui/src/app/core/state/commander-store.spec.ts`

**Interfaces:**

- Consumes: Task 7 camel-case JSON shape.
- Adds: `CommanderApiPort.getSystemMetrics(): Promise<SystemMetricsDto>`.
- Produces: root-provided `SystemMetricsStore` with `start`, `stop`, `refresh`, `state`, `effectiveSnapshot`, and `effectiveState`.
- Produces: standalone `ByteRatePipe` for Task 9.

- [ ] **Step 1: Write failing transport and formatter tests**

```typescript
it('requests the cached system snapshot from the read-only route', async () => {
  const result = api.getSystemMetrics();
  const request = http.expectOne('/api/system-metrics');

  expect(request.request.method).toBe('GET');
  expect(request.request.params.keys()).toEqual([]);
  request.flush(systemMetricsResponse());

  await expect(result).resolves.toEqual(systemMetricsResponse());
});
```

```typescript
import { ByteRatePipe } from './byte-rate.pipe';

describe('ByteRatePipe', () => {
  const pipe = new ByteRatePipe();

  it('formats unavailable, bytes, and IEC rates compactly', () => {
    expect(pipe.transform(null)).toBe('—');
    expect(pipe.transform(512)).toBe('512 B/s');
    expect(pipe.transform(1536)).toBe('1.5 KiB/s');
    expect(pipe.transform(2 * 1024 ** 2)).toBe('2.0 MiB/s');
  });
});
```

- [ ] **Step 2: Write failing polling and stale-state tests**

Use `vi.useFakeTimers()` and a deferred fake API:

```typescript
describe('SystemMetricsStore', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-08-19T12:00:00Z'));
  });

  afterEach(() => vi.useRealTimers());

  it('loads immediately, then polls once every five seconds without overlap', async () => {
    const first = deferred<SystemMetricsDto>();
    api.metricsHandler = () => first.promise;
    store.start();

    expect(api.metricsRequests).toBe(1);
    await vi.advanceTimersByTimeAsync(10_000);
    expect(api.metricsRequests).toBe(1);

    first.resolve(systemMetricsResponse());
    await Promise.resolve();
    await vi.advanceTimersByTimeAsync(4_999);
    expect(api.metricsRequests).toBe(1);
    await vi.advanceTimersByTimeAsync(1);
    expect(api.metricsRequests).toBe(2);
  });

  it('preserves the last snapshot and derives stale after fifteen seconds of failures', async () => {
    api.metricsHandler = () => Promise.resolve(systemMetricsResponse({
      sampledAt: '2026-08-19T12:00:00Z',
      state: 'healthy',
    }));
    store.start();
    await Promise.resolve();

    api.metricsHandler = () => Promise.reject(new Error('offline'));
    await vi.advanceTimersByTimeAsync(16_000);

    expect(store.effectiveSnapshot()).not.toBeNull();
    expect(store.effectiveState()).toBe('stale');
    expect(store.state().errorCode).toBe('request_failed');
  });

  it('queues one immediate refresh on visibility return without overlapping the in-flight request', async () => {
    const first = deferred<SystemMetricsDto>();
    const second = deferred<SystemMetricsDto>();
    api.metricsHandler = () => api.metricsRequests === 1 ? first.promise : second.promise;
    store.start();
    Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'visible' });
    document.dispatchEvent(new Event('visibilitychange'));

    expect(api.metricsRequests).toBe(1);
    first.resolve(systemMetricsResponse({ sampledAt: '2026-08-19T11:59:55Z' }));
    await Promise.resolve();
    expect(api.metricsRequests).toBe(2);

    second.resolve(systemMetricsResponse({ sampledAt: '2026-08-19T12:00:05Z' }));
    await Promise.resolve();

    expect(store.state().snapshot?.sampledAt).toBe('2026-08-19T12:00:05Z');
  });

  it('discards a response from a stopped polling lifecycle', async () => {
    const late = deferred<SystemMetricsDto>();
    api.metricsHandler = () => late.promise;
    store.start();
    store.stop();
    late.resolve(systemMetricsResponse({ sampledAt: '2026-08-19T11:59:55Z' }));
    await Promise.resolve();

    expect(store.state().snapshot).toBeNull();
  });
});
```

The fake API stores request count and configurable promise handler. Provide real `deferred<T>()` and complete `systemMetricsResponse` fixture functions in the spec file.

- [ ] **Step 3: Run Angular tests and verify RED**

```powershell
Push-Location client/reach-commander-ui
npm test -- --watch=false
Pop-Location
```

Expected: TypeScript compilation fails because DTOs, API method, store, and rate pipe do not exist.

- [ ] **Step 4: Add exact client DTO contracts**

Append to `api.models.ts`:

```typescript
export type HardwareMetricsState = 'healthy' | 'partial' | 'stale' | 'disabled';
export type HardwareCollectorState = 'success' | 'unsupported' | 'unavailable' | 'timeout' | 'failed';

export interface CpuMetricsDto {
  readonly utilizationPercent: number | null;
  readonly temperatureCelsius: number | null;
  readonly warningTemperatureCelsius: number | null;
  readonly criticalTemperatureCelsius: number | null;
  readonly alarm: boolean;
  readonly fault: boolean;
}

export interface MemoryMetricsDto {
  readonly usedBytes: number | null;
  readonly availableBytes: number | null;
  readonly totalBytes: number | null;
  readonly utilizationPercent: number | null;
}

export interface StorageMetricsDto {
  readonly sourceId: string;
  readonly name: string;
  readonly isAvailable: boolean;
  readonly usedBytes: number | null;
  readonly freeBytes: number | null;
  readonly totalBytes: number | null;
  readonly utilizationPercent: number | null;
}

export interface GpuMetricsDto {
  readonly id: string;
  readonly vendor: string;
  readonly name: string;
  readonly utilizationPercent: number | null;
  readonly memoryUsedBytes: number | null;
  readonly memoryTotalBytes: number | null;
  readonly temperatureCelsius: number | null;
  readonly warningTemperatureCelsius: number | null;
  readonly criticalTemperatureCelsius: number | null;
  readonly alarm: boolean;
  readonly fault: boolean;
}

export interface FanMetricsDto {
  readonly id: string;
  readonly name: string;
  readonly revolutionsPerMinute: number | null;
  readonly alarm: boolean;
  readonly fault: boolean;
}

export interface NetworkMetricsDto {
  readonly receiveBytesPerSecond: number | null;
  readonly transmitBytesPerSecond: number | null;
}

export interface HardwareCollectorStatusDto {
  readonly collector: string;
  readonly state: HardwareCollectorState;
  readonly code: string | null;
}

export interface SystemMetricsDto {
  readonly sampledAt: string;
  readonly state: HardwareMetricsState;
  readonly hostUptimeSeconds: number | null;
  readonly cpu: CpuMetricsDto | null;
  readonly memory: MemoryMetricsDto | null;
  readonly storage: readonly StorageMetricsDto[];
  readonly gpus: readonly GpuMetricsDto[];
  readonly fans: readonly FanMetricsDto[];
  readonly network: NetworkMetricsDto | null;
  readonly collectors: readonly HardwareCollectorStatusDto[];
}
```

Add `abstract getSystemMetrics(): Promise<SystemMetricsDto>;` to `CommanderApiPort`, and implement it in `ReachCommanderApi` with the same Promise transport style as the existing methods:

```typescript
getSystemMetrics(): Promise<SystemMetricsDto> {
  return firstValueFrom(this.http.get<SystemMetricsDto>('/api/system-metrics'));
}
```

Add explicit implementations to `AppTestApi` and `FakeCommanderApi`; `FakeCommanderApi` increments `metricsRequests` and invokes its configurable `metricsHandler`, while `AppTestApi` returns a complete disabled fixture so strict compilation and unrelated tests remain green.

- [ ] **Step 5: Implement the byte-rate pipe**

```typescript
import { Pipe, PipeTransform } from '@angular/core';

@Pipe({ name: 'byteRate', standalone: true })
export class ByteRatePipe implements PipeTransform {
  transform(value: number | null): string {
    if (value === null) return '—';
    if (value < 1024) return `${Math.round(value)} B/s`;
    const units = ['KiB/s', 'MiB/s', 'GiB/s', 'TiB/s'];
    const exponent = Math.min(Math.floor(Math.log(value) / Math.log(1024)), units.length);
    return `${(value / 1024 ** exponent).toFixed(1)} ${units[exponent - 1]}`;
  }
}
```

- [ ] **Step 6: Implement isolated, non-overlapping polling state**

Define:

```typescript
export interface SystemMetricsStoreState {
  readonly snapshot: SystemMetricsDto | null;
  readonly pending: boolean;
  readonly errorCode: 'metrics_not_ready' | 'request_failed' | null;
  readonly requestToken: number;
  readonly nowEpochMilliseconds: number;
}
```

`SystemMetricsStore` is `providedIn: 'root'` and exposes readonly `state`, `effectiveSnapshot`, and `effectiveState` signals. `effectiveState` returns `disabled` unchanged, otherwise returns `stale` when `nowEpochMilliseconds - Date.parse(sampledAt) > 15_000`, otherwise the server state.

`start()` is idempotent, installs one `visibilitychange` listener, and calls `refresh()` immediately. `refresh()` starts only when there is no in-flight request; it then increments the token, updates current time/pending, and records that token for the Promise. A response applies only while the store is started and its token is current. On completion, schedule exactly one `window.setTimeout` for 5,000 ms only when the document is visible. An error preserves the previous snapshot. Map to `metrics_not_ready` only when `error instanceof HttpErrorResponse`, `error.status === 503`, and `error.error` is an object whose `code === 'metrics_not_ready'`; every other shape is `request_failed`, and no server detail is displayed.

When visibility becomes visible, clear the pending timer. If a request is in flight, increment the token to invalidate its result and set `refreshAfterCurrent=true`; when that Promise settles, issue exactly one immediate replacement request. If no request is in flight, refresh immediately. This preserves strict non-overlap. `stop()` clears the timer/listener, clears the queued-refresh flag, marks the store stopped, and increments the token so outstanding responses cannot mutate state.

- [ ] **Step 7: Run Angular tests/build and commit**

```powershell
Push-Location client/reach-commander-ui
npm test -- --watch=false
npm run build
Pop-Location
git status --short
git add client/reach-commander-ui/src/app/core/api/api.models.ts client/reach-commander-ui/src/app/core/api/reach-commander-api.ts client/reach-commander-ui/src/app/core/api/reach-commander-api.spec.ts client/reach-commander-ui/src/app/core/state/system-metrics-store.ts client/reach-commander-ui/src/app/core/state/system-metrics-store.spec.ts client/reach-commander-ui/src/app/shared/pipes/byte-rate.pipe.ts client/reach-commander-ui/src/app/shared/pipes/byte-rate.pipe.spec.ts client/reach-commander-ui/src/app/app.spec.ts client/reach-commander-ui/src/app/core/state/commander-store.spec.ts
git commit -m "feat: add live system metrics state"
```

Expected: all Angular tests and production build pass.

---

### Task 9: Add the compact top-bar widget and accessible details panel

**Files:**

- Create: `client/reach-commander-ui/src/app/features/system-metrics/system-metrics-widget.component.ts`
- Create: `client/reach-commander-ui/src/app/features/system-metrics/system-metrics-widget.component.html`
- Create: `client/reach-commander-ui/src/app/features/system-metrics/system-metrics-widget.component.scss`
- Create: `client/reach-commander-ui/src/app/features/system-metrics/system-metrics-widget.component.spec.ts`
- Create: `client/reach-commander-ui/src/app/features/system-metrics/system-metrics-details.component.ts`
- Create: `client/reach-commander-ui/src/app/features/system-metrics/system-metrics-details.component.html`
- Create: `client/reach-commander-ui/src/app/features/system-metrics/system-metrics-details.component.scss`
- Create: `client/reach-commander-ui/src/app/features/system-metrics/system-metrics-details.component.spec.ts`
- Create: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.spec.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.html`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.scss`

- [ ] **Step 1: Write failing compact-summary component tests**

Create `system-metrics-widget.component.spec.ts` with complete DTO fixtures and tests that prove:

```typescript
it('shows the compact core summary using the fullest source and busiest GPU', () => {
  fixture.componentRef.setInput('snapshot', systemMetricsResponse({
    cpu: cpu({ utilizationPercent: 18, temperatureCelsius: 54 }),
    memory: memory({ utilizationPercent: 43 }),
    storage: [storage('downloads', 52), storage('media', 71)],
    gpus: [gpu('integrated', 4), gpu('discrete', 12)],
  }));
  fixture.detectChanges();

  expect(summary()).toContain('CPU 18% · 54°C');
  expect(summary()).toContain('RAM 43%');
  expect(summary()).toContain('STORAGE 71%');
  expect(summary()).toContain('GPU 12%');
});

it('renders unavailable values as em dashes and exposes the server state', () => {
  fixture.componentRef.setInput('snapshot', systemMetricsResponse({
    state: 'partial', cpu: null, memory: null, storage: [], gpus: [],
  }));
  fixture.detectChanges();

  expect(summary()).toContain('CPU —');
  expect(summary()).toContain('RAM —');
  expect(button().getAttribute('data-state')).toBe('partial');
  expect(button().getAttribute('aria-label')).toContain('System metrics: partial');
});
```

Also test null/not-ready state (`System · Loading`), stale state (`System · Stale`), click output, `aria-expanded`, and that alarm/fault data selects the danger presentation even below numeric warning thresholds. Feed a fake state sequence and assert the widget's polite announcer emits once for partial, recovered, warning, critical, and stale transitions while repeated numeric-only snapshots emit nothing.

- [ ] **Step 2: Write failing details-panel accessibility and content tests**

Create `system-metrics-details.component.spec.ts`. Supply the opener element as an input, open the panel, and prove:

```typescript
it('renders every metric family and collector availability without inventing values', () => {
  // Fixture contains two sources, two GPUs, fans, network, uptime, and one unavailable collector.
  expect(screenText()).toContain('System metrics');
  expect(screenText()).toContain('CPU');
  expect(screenText()).toContain('Memory');
  expect(screenText()).toContain('Storage');
  expect(screenText()).toContain('Graphics');
  expect(screenText()).toContain('Fans');
  expect(screenText()).toContain('Network');
  expect(screenText()).toContain('Uptime');
  expect(screenText()).toContain('unavailable');
  expect(screenText()).toContain('—');
});

it('closes on Escape and restores focus to the opener', () => {
  opener.focus();
  document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
  expect(closed.emit).toHaveBeenCalledOnce();
  expect(document.activeElement).toBe(opener);
});
```

Test `role="dialog"`, `aria-modal="true"`, an accessible title, backdrop click, close-button click, first-focus behavior, and tab wrapping. Use `A11yModule` and `cdkTrapFocus`/`cdkTrapFocusAutoCapture`; do not implement a bespoke focus trap.

- [ ] **Step 3: Write a failing shell lifecycle/integration test**

Create `commander-shell.component.spec.ts` with fakes for `CommanderStore`, `CommanderKeyboardService`, and `SystemMetricsStore`. Assert that:

- the widget is the last child of `.top-actions`;
- clicking its button opens the details panel;
- `SystemMetricsStore.start()` is called once on shell initialization;
- `SystemMetricsStore.stop()` is called when the shell is destroyed;
- opening/closing the panel does not start another polling loop.

- [ ] **Step 4: Run the focused Angular tests and verify RED**

```powershell
Push-Location client/reach-commander-ui
npm test -- --watch=false --include='src/app/features/system-metrics/**/*.spec.ts' --include='src/app/features/commander/commander-shell/commander-shell.component.spec.ts'
Pop-Location
```

Expected: compilation fails because the new components and shell integration do not exist.

- [ ] **Step 5: Implement the compact widget**

`SystemMetricsWidgetComponent` is standalone, `OnPush`, and accepts:

```typescript
readonly snapshot = input.required<SystemMetricsDto | null>();
readonly effectiveState = input.required<HardwareMetricsState | 'loading'>();
readonly expanded = input(false);
readonly openDetails = output<void>();
```

Use computed values to select the highest non-null storage utilization and highest non-null GPU utilization. Do not sum devices or substitute zero for missing sensors. Format the visible wide label as:

```text
CPU 18% · 54°C | RAM 43% | STORAGE 71% | GPU 12%
```

When CPU utilization exists but its temperature does not, render `CPU 18%` without an empty separator. At narrow widths, hide the detailed spans and keep a stable `System` label plus the state indicator. Apply thresholds consistently:

- warning at utilization `>= 80%`;
- danger at utilization `>= 95%`;
- warning/danger temperature from the supplied per-device thresholds only;
- `alarm` or `fault` always produces danger;
- unknown values remain neutral.

The widget is a real button with `data-testid="system-metrics-trigger"`, `aria-expanded`, `aria-haspopup="dialog"`, a full spoken summary, and `data-state`. Keep a separate visually hidden polite live region in this always-mounted component, driven by a remembered presentation state. It announces healthy/partial/stale/disabled transitions, recovery to healthy, and entry into warning/critical severity, but never announces a repeated state or a numeric-only five-second update.

- [ ] **Step 6: Implement the details panel**

`SystemMetricsDetailsComponent` is standalone, `OnPush`, imports `A11yModule`, existing `FileSizePipe`, and `ByteRatePipe`, and accepts the current snapshot, effective state, current client time, and opener. It emits `closed`.

Render a right-aligned overlay and backdrop with these groups:

- overall state, sampled age, and host uptime;
- CPU utilization and temperature;
- RAM used/total/utilization;
- every configured source with available/unavailable state and used/free/total;
- every detected GPU with vendor/name, utilization, memory, and temperature;
- every fan RPM/status;
- aggregate receive/transmit rates;
- collectors not in `success`, showing only safe collector name, state, and stable code.

Use semantic headings, progress bars with `aria-valuemin`, `aria-valuemax`, and `aria-valuenow` only when values exist, and visible text for missing readings. Close on backdrop, close button, and Escape; trap focus while open and restore focus to the trigger after closing.

- [ ] **Step 7: Integrate the store and widget into the shell**

Inject `SystemMetricsStore` into `CommanderShellComponent`. Call `start()` in `ngOnInit` and `stop()` from the existing `DestroyRef` callback. Add a `metricsOpen` signal and trigger `ElementRef` reference.

Import the new widget and details components. Append the widget after the existing settings button in `.top-actions`; conditionally render the details panel adjacent to the header so it overlays the panes without changing their layout. Update responsive styles so the control remains at the far-right edge, collapses to the compact `System` label below the existing breakpoint, and the panel remains within the viewport at phone widths.

At the start of `execute`, intercept a commander `escape` command while `metricsOpen()` is true, call the same `closeMetrics()` method as the panel, and return before changing filters/selections/menu state. The details component's Escape listener also calls that idempotent close path and restores focus, so event-listener ordering cannot leak Escape into the file-manager state.

- [ ] **Step 8: Run Angular verification and commit**

```powershell
Push-Location client/reach-commander-ui
npm test -- --watch=false
npm run build
Pop-Location
git status --short
git add client/reach-commander-ui/src/app/features/system-metrics client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.ts client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.html client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.scss client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.spec.ts
git commit -m "feat: add system metrics dashboard"
```

Expected: all Angular tests and the production build pass; keyboard-only operation can open, inspect, and close the panel.

---

### Task 10: Add opt-in container telemetry, operations docs, and end-to-end verification

**Files:**

- Create: `compose.hardware.yaml`
- Create: `compose.hardware.dri.yaml`
- Create: `compose.hardware.nvidia.yaml`
- Modify: `Dockerfile`
- Modify: `README.md`
- Create: `tests/e2e/specs/system-metrics.spec.ts`
- Modify: `tests/e2e/support/seed-fixtures.ts`

- [ ] **Step 1: Write the hardware-independent end-to-end test**

Create `system-metrics.spec.ts`. The test must accept either real metrics or an explicit unavailable/partial state; it must not assert machine-specific percentages, device names, temperatures, or fan counts.

In `seed-fixtures.ts`, add `HardwareMetrics__Enabled: 'false'` to the spawned server environment. The hosted service then publishes a deterministic disabled snapshot without probing the E2E runner's hardware; native Windows and Ubuntu runtime behavior is covered by the separate smoke checks below.

```typescript
test('opens live system metrics from the top-right control', async ({ page }) => {
  await page.goto('/');
  const trigger = page.getByTestId('system-metrics-trigger');
  await expect(trigger).toBeVisible();
  await expect(trigger).toHaveAttribute('aria-haspopup', 'dialog');
  await expect(trigger).toHaveAttribute('data-state', 'disabled');

  await trigger.click();
  const panel = page.getByRole('dialog', { name: 'System metrics' });
  await expect(panel).toBeVisible();
  await expect(panel.getByText('CPU', { exact: true })).toBeVisible();
  await expect(panel.getByText('Memory', { exact: true })).toBeVisible();

  await page.keyboard.press('Escape');
  await expect(panel).toBeHidden();
  await expect(trigger).toBeFocused();
});
```

Add a second test at a `390 x 844` viewport that opens the panel and asserts its bounding box stays inside the viewport and the trigger still exposes the compact `System` label.

- [ ] **Step 2: Add opt-in read-only host views to a Compose override**

Create `compose.hardware.yaml`:

```yaml
services:
  reachcommander:
    environment:
      HardwareMetrics__ProcRoot: /host/proc
      HardwareMetrics__SysRoot: /host/sys
    volumes:
      - type: bind
        source: /proc/stat
        target: /host/proc/stat
        read_only: true
      - type: bind
        source: /proc/meminfo
        target: /host/proc/meminfo
        read_only: true
      - type: bind
        source: /proc/uptime
        target: /host/proc/uptime
        read_only: true
      - type: bind
        source: /proc/net/dev
        target: /host/proc/net/dev
        read_only: true
      - type: bind
        source: /sys
        target: /host/sys
        read_only: true
```

This override is optional. Keep the default `compose.yaml` unchanged: read-only root filesystem, all capabilities dropped, no new privileges, no Docker socket, no privileged mode, and no host PID namespace.

- [ ] **Step 3: Add explicit GPU-device overrides**

Create `compose.hardware.dri.yaml` for AMD/Intel DRM access:

```yaml
services:
  reachcommander:
    devices:
      - /dev/dri:/dev/dri
    group_add:
      - "${RENDER_GID}"
```

Create `compose.hardware.nvidia.yaml` for NVIDIA hosts:

```yaml
services:
  reachcommander:
    gpus: all
```

The NVIDIA override requires NVIDIA Container Toolkit on the Ubuntu host. Do not add vendor CLIs, a Docker-socket proxy, extra capabilities, or privileged mode. GPU metrics remain optional if runtime libraries or devices are unavailable.

- [ ] **Step 4: Prepare trusted mount targets in the runtime image**

Before `USER 1000:1000` in `Dockerfile`, add:

```dockerfile
RUN mkdir -p /host/proc/net /host/sys
```

Do not install hardware-control utilities. LibreHardwareMonitor is a managed build dependency used only by the Windows collector; the Linux image uses procfs, sysfs/DRM, and an already-present vendor NVML library when the NVIDIA runtime injects it.

- [ ] **Step 5: Document Windows development and Ubuntu deployment**

Add a `Hardware monitoring` section to `README.md` covering:

- native Windows development: run `dotnet run --project src/ReachCommander.Api`; metrics describe the Windows workstation and unsupported sensors show as unavailable;
- Docker Desktop development: the Linux container describes its Linux VM/container view, not all Windows host sensors; use the native Windows run for full workstation metrics;
- Ubuntu native deployment and default hardened Compose deployment;
- opt-in commands:

```bash
docker compose -f compose.yaml -f compose.hardware.yaml up -d --build
RENDER_GID="$(getent group render | cut -d: -f3)" docker compose -f compose.yaml -f compose.hardware.yaml -f compose.hardware.dri.yaml up -d --build
docker compose -f compose.yaml -f compose.hardware.yaml -f compose.hardware.nvidia.yaml up -d --build
```

- each mount/device, why it exists, and how to omit it;
- the five-second cadence, 15-second stale threshold, stable unavailable states, and `/api/system-metrics`;
- that this milestone stores no history and provides no fan/GPU/power control;
- the endpoint reveals capacity and device inventory, so ReachCommander remains for trusted networks and should be protected by the deployment's existing access controls.

- [ ] **Step 6: Validate Compose expansion and repository safety invariants**

Run from the repository root:

```powershell
docker compose config
docker compose -f compose.yaml -f compose.hardware.yaml config
$env:RENDER_GID='109'
docker compose -f compose.yaml -f compose.hardware.yaml -f compose.hardware.dri.yaml config
docker compose -f compose.yaml -f compose.hardware.yaml -f compose.hardware.nvidia.yaml config
Remove-Item Env:RENDER_GID
rg -n -g "compose*.yaml" "privileged:|pid:\s*host|/var/run/docker.sock|SYS_ADMIN|/dev/mem" .
rg -n "privileged:|pid:\s*host|/var/run/docker.sock|SYS_ADMIN|/dev/mem" Dockerfile README.md
```

Expected: every Compose combination renders successfully; the search returns only explanatory documentation saying those privileges are not used, never an enabled setting.

- [ ] **Step 7: Run the full automated verification matrix**

```powershell
dotnet restore ReachCommander.slnx
dotnet test ReachCommander.slnx --configuration Release --no-restore
dotnet publish src/ReachCommander.Api/ReachCommander.Api.csproj --configuration Release --no-restore -p:BuildAngularOnPublish=false
dotnet list src/ReachCommander.Api/ReachCommander.Api.csproj package --include-transitive
Push-Location client/reach-commander-ui
npm test -- --watch=false
npm run build
Pop-Location
Push-Location tests/e2e
npm test
Pop-Location
```

Expected: all .NET, Angular, and Playwright tests pass; the published app contains the client; the package graph contains exactly the reviewed hardware dependency and no command-execution helper.

- [ ] **Step 8: Smoke-test the endpoint on the available platforms**

On the Windows development machine:

```powershell
dotnet run --project src/ReachCommander.Api
```

In a second terminal, wait for health and two sampling intervals, then run:

```powershell
Invoke-RestMethod http://localhost:5000/health
Invoke-RestMethod http://localhost:5000/api/system-metrics | ConvertTo-Json -Depth 6
```

Use the actual HTTPS/HTTP launch-profile URL printed by `dotnet run` if it differs. Confirm the response contains only the documented DTO fields and that missing sensors are null/unavailable instead of failing the endpoint.

When Docker is available, run:

```powershell
docker compose -f compose.yaml -f compose.hardware.yaml up -d --build
Invoke-RestMethod http://localhost:8092/health
Invoke-RestMethod http://localhost:8092/api/system-metrics | ConvertTo-Json -Depth 6
docker compose -f compose.yaml -f compose.hardware.yaml down
```

Repeat on the Ubuntu deployment host with only the applicable DRI or NVIDIA override. Do not make absence of Docker, a GPU, a temperature sensor, or a fan controller fail the platform-neutral automated suite; record those smoke checks as environment-specific verification.

- [ ] **Step 9: Perform visual and security-focused acceptance checks**

Run the app and inspect it at `1440 x 900` and `390 x 844` with Playwright or the in-app browser. Capture screenshots outside tracked source directories and verify:

- the compact widget stays at the far-right of the top bar and does not obscure existing actions;
- the details panel stays within the viewport, scrolls internally, and does not resize either file pane;
- partial, stale, alarm/fault, and unavailable states are distinguishable without relying on color alone;
- keyboard focus is visible, trapped while open, and restored on close.

Also inspect the API payload and search the implementation:

```powershell
rg -n "serial|machine.?id|hostname|process|command.?line|environment" src/ReachCommander.Api src/ReachCommander.Application src/ReachCommander.Infrastructure client/reach-commander-ui/src/app/features/system-metrics
rg -n "Process\.Start|cmd\.exe|powershell|/bin/sh|File\.Write|WriteAll|Set.*Fan|Set.*Clock|Set.*Power" src/ReachCommander.Infrastructure
git diff --check
git status --short
```

Expected: DTOs expose no prohibited identifiers/process data, collectors start no shells or vendor commands, collectors perform no hardware writes, formatting is clean, and only planned files are modified.

- [ ] **Step 10: Commit the deployment and acceptance slice**

```powershell
git add Dockerfile README.md compose.hardware.yaml compose.hardware.dri.yaml compose.hardware.nvidia.yaml tests/e2e/specs/system-metrics.spec.ts tests/e2e/support/seed-fixtures.ts
git commit -m "docs: add hardware monitoring deployment"
git status --short
```

Expected: the final status contains no uncommitted files from this implementation, aside from unrelated user-owned changes that were present before the work began.

---

## Final plan self-review checklist

Before declaring implementation complete:

- [ ] Map every approved design requirement to at least one task and verification step.
- [ ] Confirm Windows native development and Ubuntu native/container deployment are both represented.
- [ ] Confirm automated tests use injected platforms, fixture files, and fake sensor APIs—never the executing machine's hardware.
- [ ] Confirm the public DTO contains no hostname, serial number, process, command-line, environment-variable, or arbitrary filesystem-path data.
- [ ] Confirm default Compose security remains unchanged and host mounts/devices are explicit opt-in overrides.
- [ ] Confirm CPU/RAM/storage work without optional GPU/temperature/fan support and optional collectors degrade independently.
- [ ] Confirm the sampler has one non-overlapping cadence and the browser store has one non-overlapping polling lifecycle.
- [ ] Confirm stale state preserves the last good values and visible state changes are accessible without numeric live-region noise.
- [ ] Confirm all snippets use the same type/property names as the file structure and neighboring tasks.
- [ ] Search the plan for unfinished markers or omitted implementation steps and replace every incomplete instruction.
- [ ] Run every verification command that is available locally and clearly record any platform-only smoke check for Ubuntu.

using System.Globalization;
using System.Security;
using Microsoft.Extensions.Options;
using ReachCommander.Application.SystemMetrics;
using ReachCommander.Infrastructure.SystemMetrics.Linux;

namespace ReachCommander.Infrastructure.SystemMetrics.Gpu;

internal sealed class LinuxDrmGpuCollector(
    IOptions<HardwareMetricsOptions> options,
    BoundedTextFileReader reader,
    ITrustedPathResolver pathResolver,
    IHostPlatform platform) : IHardwareMetricsCollector
{
    private const int MaximumDirectoriesExamined = 256;

    public string Name => "linux-drm-gpu";
    public bool IsSupported => platform.IsLinux && options.Value.GpusEnabled;

    public async ValueTask<HardwareMetricsContribution> CollectAsync(
        CancellationToken cancellationToken)
    {
        if (!IsSupported)
        {
            return HardwareMetricsContribution.Unsupported(Name);
        }

        try
        {
            var sysRoot = options.Value.LinuxSysRoot;
            var drmRoot = Path.Combine(sysRoot, "class", "drm");
            if (!Directory.Exists(drmRoot))
            {
                return HardwareMetricsContribution.Unsupported(Name);
            }

            var cards = EnumerateIndexedDirectories(drmRoot, "card", MetricNormalizer.MaximumGpuCount);
            var seenDevices = new HashSet<string>(StringComparer.Ordinal);
            var gpus = new List<GpuMetrics>(MetricNormalizer.MaximumGpuCount);

            foreach (var card in cards)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var device = Path.Combine(card, "device");
                if (!Directory.Exists(device))
                {
                    continue;
                }

                EnsureTrusted(sysRoot, card);
                EnsureTrusted(sysRoot, device);
                if (!seenDevices.Add(pathResolver.GetCanonicalPath(device)))
                {
                    continue;
                }

                var vendorText = await ReadOptionalTextAsync(
                    sysRoot,
                    Path.Combine(device, "vendor"),
                    cancellationToken);
                var vendor = vendorText is null ? null : GpuVendorIds.Parse(vendorText);
                if (vendor is not (GpuVendor.Amd or GpuVendor.Intel))
                {
                    continue;
                }

                var utilization = MetricNormalizer.Percent(await ReadOptionalDoubleAsync(
                    sysRoot,
                    Path.Combine(device, "gpu_busy_percent"),
                    cancellationToken));
                var used = MetricNormalizer.NonNegative(await ReadOptionalLongAsync(
                    sysRoot,
                    Path.Combine(device, "mem_info_vram_used"),
                    cancellationToken));
                var total = MetricNormalizer.NonNegative(await ReadOptionalLongAsync(
                    sysRoot,
                    Path.Combine(device, "mem_info_vram_total"),
                    cancellationToken));
                if (used is not null && total is not null && used > total)
                {
                    used = null;
                }

                var thermal = await ReadThermalAsync(sysRoot, device, cancellationToken);
                var ordinal = gpus.Count + 1;
                var vendorName = GpuVendorIds.DisplayName(vendor.Value);
                var vendorId = vendor.Value == GpuVendor.Amd ? "amd" : "intel";
                gpus.Add(new GpuMetrics(
                    $"gpu-{vendorId}-{ordinal:D3}",
                    vendorName,
                    $"{vendorName} GPU {ordinal}",
                    utilization,
                    used,
                    total,
                    thermal?.TemperatureCelsius,
                    thermal?.WarningTemperatureCelsius,
                    thermal?.CriticalTemperatureCelsius,
                    thermal?.Alarm ?? false,
                    thermal?.Fault ?? false));
            }

            if (gpus.Count == 0)
            {
                return HardwareMetricsContribution.Unsupported(Name);
            }

            return new HardwareMetricsContribution(
                new HardwareCollectorStatus(Name, HardwareCollectorState.Success, null),
                Gpus: Array.AsReadOnly(gpus.ToArray()));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SysfsEscapeException)
        {
            return Failed(HardwareCollectorState.Failed, "metrics_source_escape");
        }
        catch (InvalidDataException)
        {
            return Failed(HardwareCollectorState.Failed, "metrics_input_invalid");
        }
        catch (FormatException)
        {
            return Failed(HardwareCollectorState.Failed, "metrics_input_invalid");
        }
        catch (OverflowException)
        {
            return Failed(HardwareCollectorState.Failed, "metrics_input_invalid");
        }
        catch (UnauthorizedAccessException)
        {
            return Failed(HardwareCollectorState.Unavailable, "gpu_access_unavailable");
        }
        catch (SecurityException)
        {
            return Failed(HardwareCollectorState.Unavailable, "gpu_access_unavailable");
        }
        catch (IOException)
        {
            return Failed(HardwareCollectorState.Unavailable, "gpu_access_unavailable");
        }
    }

    private async ValueTask<GpuThermal?> ReadThermalAsync(
        string sysRoot,
        string device,
        CancellationToken cancellationToken)
    {
        var hwmonRoot = Path.Combine(device, "hwmon");
        foreach (var directory in EnumerateIndexedDirectories(hwmonRoot, "hwmon", 16))
        {
            EnsureTrusted(sysRoot, directory);
            var input = await ReadOptionalLongAsync(
                sysRoot,
                Path.Combine(directory, "temp1_input"),
                cancellationToken);
            if (input is null)
            {
                continue;
            }

            var fault = await ReadOptionalBooleanAsync(
                sysRoot,
                Path.Combine(directory, "temp1_fault"),
                cancellationToken);
            var temperature = fault
                ? null
                : MetricNormalizer.MillidegreesCelsius(input.Value);
            return new GpuThermal(
                temperature,
                await ReadOptionalCelsiusAsync(
                    sysRoot,
                    Path.Combine(directory, "temp1_max"),
                    cancellationToken),
                await ReadOptionalCelsiusAsync(
                    sysRoot,
                    Path.Combine(directory, "temp1_crit"),
                    cancellationToken),
                await ReadOptionalBooleanAsync(
                    sysRoot,
                    Path.Combine(directory, "temp1_alarm"),
                    cancellationToken),
                fault);
        }

        return null;
    }

    private async ValueTask<double?> ReadOptionalCelsiusAsync(
        string sysRoot,
        string path,
        CancellationToken cancellationToken)
    {
        var value = await ReadOptionalLongAsync(sysRoot, path, cancellationToken);
        return value is null ? null : MetricNormalizer.MillidegreesCelsius(value.Value);
    }

    private async ValueTask<bool> ReadOptionalBooleanAsync(
        string sysRoot,
        string path,
        CancellationToken cancellationToken) =>
        await ReadOptionalLongAsync(sysRoot, path, cancellationToken) is > 0;

    private async ValueTask<long?> ReadOptionalLongAsync(
        string sysRoot,
        string path,
        CancellationToken cancellationToken)
    {
        var text = await ReadOptionalTextAsync(sysRoot, path, cancellationToken);
        if (text is null)
        {
            return null;
        }

        return long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new FormatException("GPU counter is invalid.");
    }

    private async ValueTask<double?> ReadOptionalDoubleAsync(
        string sysRoot,
        string path,
        CancellationToken cancellationToken)
    {
        var text = await ReadOptionalTextAsync(sysRoot, path, cancellationToken);
        if (text is null)
        {
            return null;
        }

        return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new FormatException("GPU utilization is invalid.");
    }

    private async ValueTask<string?> ReadOptionalTextAsync(
        string sysRoot,
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        EnsureTrusted(sysRoot, path);
        try
        {
            return await reader.ReadRequiredAsync(path, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private void EnsureTrusted(string root, string candidate)
    {
        if (!pathResolver.IsWithinRoot(root, candidate))
        {
            throw new SysfsEscapeException();
        }
    }

    private static IReadOnlyList<string> EnumerateIndexedDirectories(
        string root,
        string prefix,
        int maximumMatches)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        var matches = new List<string>(maximumMatches);
        var examined = 0;
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            examined++;
            if (examined > MaximumDirectoriesExamined)
            {
                throw new InvalidDataException("GPU directory count exceeds its limit.");
            }

            var name = Path.GetFileName(directory);
            if (!name.StartsWith(prefix, StringComparison.Ordinal) ||
                !int.TryParse(name[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                continue;
            }

            matches.Add(directory);
            if (matches.Count == maximumMatches)
            {
                break;
            }
        }

        matches.Sort(StringComparer.Ordinal);
        return matches;
    }

    private HardwareMetricsContribution Failed(HardwareCollectorState state, string code) => new(
        new HardwareCollectorStatus(Name, state, code));

    private sealed record GpuThermal(
        double? TemperatureCelsius,
        double? WarningTemperatureCelsius,
        double? CriticalTemperatureCelsius,
        bool Alarm,
        bool Fault);

    private sealed class SysfsEscapeException : Exception;
}

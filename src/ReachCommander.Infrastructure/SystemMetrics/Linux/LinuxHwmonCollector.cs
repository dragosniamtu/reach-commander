using System.Globalization;
using System.Security;
using Microsoft.Extensions.Options;
using ReachCommander.Application.SystemMetrics;

namespace ReachCommander.Infrastructure.SystemMetrics.Linux;

internal sealed class LinuxHwmonCollector(
    IOptions<HardwareMetricsOptions> options,
    BoundedTextFileReader reader,
    ITrustedPathResolver pathResolver,
    IHostPlatform platform) : IHardwareMetricsCollector
{
    private const int MaximumDirectoriesExamined = 1024;
    private const int MaximumThermalTripPoints = 32;
    private static readonly string[] CpuChipNames =
        ["coretemp", "k10temp", "zenpower", "peci_cputemp"];
    private static readonly string[] PreferredTemperatureLabels =
        ["package", "tctl", "die", "cpu"];
    private static readonly string[] CpuThermalZoneNames =
        ["x86_pkg_temp", "cpu", "soc", "package"];

    public string Name => "linux-hwmon";
    public bool IsSupported => platform.IsLinux;

    public async ValueTask<HardwareMetricsContribution> CollectAsync(
        CancellationToken cancellationToken)
    {
        if (!IsSupported)
        {
            return HardwareMetricsContribution.Unsupported(Name);
        }

        if (!options.Value.TemperaturesEnabled && !options.Value.FansEnabled)
        {
            return Success(null, []);
        }

        try
        {
            var sysRoot = options.Value.LinuxSysRoot;
            var hwmonDirectories = EnumerateIndexedDirectories(
                Path.Combine(sysRoot, "class", "hwmon"),
                "hwmon",
                MetricNormalizer.MaximumSensorsPerFamily);
            var thermalDirectories = EnumerateIndexedDirectories(
                Path.Combine(sysRoot, "class", "thermal"),
                "thermal_zone",
                MetricNormalizer.MaximumSensorsPerFamily);

            if (hwmonDirectories.Count == 0 && thermalDirectories.Count == 0)
            {
                return HardwareMetricsContribution.Unsupported(Name);
            }

            var seenDevices = new HashSet<string>(StringComparer.Ordinal);
            var temperatures = new List<TemperatureCandidate>();
            var fans = new List<FanMetrics>();

            foreach (var directory in hwmonDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureTrusted(sysRoot, directory);
                if (!seenDevices.Add(pathResolver.GetCanonicalPath(directory)))
                {
                    continue;
                }

                await ReadHwmonDeviceAsync(
                    sysRoot,
                    directory,
                    temperatures,
                    fans,
                    cancellationToken);
            }

            if (options.Value.TemperaturesEnabled)
            {
                foreach (var directory in thermalDirectories)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    EnsureTrusted(sysRoot, directory);
                    if (!seenDevices.Add(pathResolver.GetCanonicalPath(directory)))
                    {
                        continue;
                    }

                    var candidate = await ReadThermalZoneAsync(
                        sysRoot,
                        directory,
                        cancellationToken);
                    if (candidate is not null)
                    {
                        temperatures.Add(candidate);
                    }
                }
            }

            var selectedTemperature = temperatures
                .OrderBy(candidate => candidate.TemperatureCelsius is null ? 1 : 0)
                .ThenBy(candidate => candidate.Priority)
                .FirstOrDefault();
            CpuMetrics? cpu = selectedTemperature is null
                ? null
                : new CpuMetrics(
                    null,
                    selectedTemperature.TemperatureCelsius,
                    selectedTemperature.WarningTemperatureCelsius,
                    selectedTemperature.CriticalTemperatureCelsius,
                    selectedTemperature.Alarm,
                    selectedTemperature.Fault);

            if (cpu is null && fans.Count == 0)
            {
                return HardwareMetricsContribution.Unsupported(Name);
            }

            return Success(cpu, fans);
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
            return Failed(HardwareCollectorState.Unavailable, "metrics_input_unavailable");
        }
        catch (SecurityException)
        {
            return Failed(HardwareCollectorState.Unavailable, "metrics_input_unavailable");
        }
        catch (IOException)
        {
            return Failed(HardwareCollectorState.Unavailable, "metrics_input_unavailable");
        }
    }

    private async ValueTask ReadHwmonDeviceAsync(
        string sysRoot,
        string directory,
        ICollection<TemperatureCandidate> temperatures,
        ICollection<FanMetrics> fans,
        CancellationToken cancellationToken)
    {
        var chipName = (await ReadOptionalTextAsync(
            sysRoot,
            Path.Combine(directory, "name"),
            cancellationToken))?.Trim() ?? string.Empty;
        var cpuChip = CpuChipNames.Contains(chipName, StringComparer.OrdinalIgnoreCase);

        for (var index = 1; index <= MetricNormalizer.MaximumSensorsPerFamily; index++)
        {
            if (options.Value.TemperaturesEnabled && cpuChip)
            {
                var input = await ReadOptionalLongAsync(
                    sysRoot,
                    Path.Combine(directory, $"temp{index}_input"),
                    cancellationToken);
                if (input is not null)
                {
                    var temperatureLabel = await ReadOptionalTextAsync(
                        sysRoot,
                        Path.Combine(directory, $"temp{index}_label"),
                        cancellationToken);
                    var alarm = await ReadOptionalBooleanAsync(
                        sysRoot,
                        Path.Combine(directory, $"temp{index}_alarm"),
                        cancellationToken);
                    var fault = await ReadOptionalBooleanAsync(
                        sysRoot,
                        Path.Combine(directory, $"temp{index}_fault"),
                        cancellationToken);
                    var temperature = fault
                        ? null
                        : MetricNormalizer.MillidegreesCelsius(input.Value);
                    if (temperature is not null || alarm || fault)
                    {
                        temperatures.Add(new TemperatureCandidate(
                            temperature,
                            await ReadOptionalCelsiusAsync(
                                sysRoot,
                                Path.Combine(directory, $"temp{index}_max"),
                                cancellationToken),
                            await ReadOptionalCelsiusAsync(
                                sysRoot,
                                Path.Combine(directory, $"temp{index}_crit"),
                                cancellationToken),
                            alarm,
                            fault,
                            HasPreferredTemperatureLabel(temperatureLabel) ? 0 : 1));
                    }
                }
            }

            if (!options.Value.FansEnabled || fans.Count >= MetricNormalizer.MaximumSensorsPerFamily)
            {
                continue;
            }

            var fanInput = await ReadOptionalLongAsync(
                sysRoot,
                Path.Combine(directory, $"fan{index}_input"),
                cancellationToken);
            if (fanInput is null)
            {
                continue;
            }

            var fanAlarm = await ReadOptionalBooleanAsync(
                sysRoot,
                Path.Combine(directory, $"fan{index}_alarm"),
                cancellationToken);
            var fanFault = await ReadOptionalBooleanAsync(
                sysRoot,
                Path.Combine(directory, $"fan{index}_fault"),
                cancellationToken);
            var rpm = !fanFault && fanInput is >= 0 and <= int.MaxValue
                ? (int?)fanInput.Value
                : null;
            var fanLabel = await ReadOptionalTextAsync(
                sysRoot,
                Path.Combine(directory, $"fan{index}_label"),
                cancellationToken);
            var ordinal = fans.Count + 1;
            fans.Add(new FanMetrics(
                $"fan-{ordinal:D3}",
                MetricNormalizer.Label(fanLabel, $"{chipName} Fan {index}"),
                rpm,
                fanAlarm,
                fanFault));
        }
    }

    private async ValueTask<TemperatureCandidate?> ReadThermalZoneAsync(
        string sysRoot,
        string directory,
        CancellationToken cancellationToken)
    {
        var type = await ReadOptionalTextAsync(
            sysRoot,
            Path.Combine(directory, "type"),
            cancellationToken);
        if (type is null || !CpuThermalZoneNames.Any(
                candidate => type.Contains(candidate, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var input = await ReadOptionalLongAsync(
            sysRoot,
            Path.Combine(directory, "temp"),
            cancellationToken);
        if (input is null)
        {
            return null;
        }

        double? warning = null;
        double? critical = null;
        for (var index = 0; index < MaximumThermalTripPoints; index++)
        {
            var tripType = await ReadOptionalTextAsync(
                sysRoot,
                Path.Combine(directory, $"trip_point_{index}_type"),
                cancellationToken);
            var tripTemperature = await ReadOptionalCelsiusAsync(
                sysRoot,
                Path.Combine(directory, $"trip_point_{index}_temp"),
                cancellationToken);
            if (tripType is null || tripTemperature is null)
            {
                continue;
            }

            if (tripType.Contains("critical", StringComparison.OrdinalIgnoreCase))
            {
                critical = Min(warning: critical, candidate: tripTemperature.Value);
            }
            else if (tripType.Contains("passive", StringComparison.OrdinalIgnoreCase) ||
                     tripType.Contains("hot", StringComparison.OrdinalIgnoreCase))
            {
                warning = Min(warning, tripTemperature.Value);
            }
        }

        var temperature = MetricNormalizer.MillidegreesCelsius(input.Value);
        return temperature is null
            ? null
            : new TemperatureCandidate(temperature, warning, critical, false, false, 10);
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

        if (!long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw new FormatException("Hardware sensor value is invalid.");
        }

        return value;
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
                throw new InvalidDataException("Hardware directory count exceeds its limit.");
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

    private static bool HasPreferredTemperatureLabel(string? label) =>
        label is not null && PreferredTemperatureLabels.Any(
            candidate => label.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    private static double? Min(double? warning, double candidate) =>
        warning is null ? candidate : Math.Min(warning.Value, candidate);

    private HardwareMetricsContribution Success(CpuMetrics? cpu, IReadOnlyList<FanMetrics> fans) => new(
        new HardwareCollectorStatus(Name, HardwareCollectorState.Success, null),
        Cpu: cpu,
        Fans: Array.AsReadOnly(fans.ToArray()));

    private HardwareMetricsContribution Failed(HardwareCollectorState state, string code) => new(
        new HardwareCollectorStatus(Name, state, code));

    private sealed record TemperatureCandidate(
        double? TemperatureCelsius,
        double? WarningTemperatureCelsius,
        double? CriticalTemperatureCelsius,
        bool Alarm,
        bool Fault,
        int Priority);

    private sealed class SysfsEscapeException : Exception;
}
